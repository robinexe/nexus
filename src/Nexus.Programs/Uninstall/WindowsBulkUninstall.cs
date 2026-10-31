using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Nexus.Programs.Inventory;

namespace Nexus.Programs.Uninstall;

/// <summary>
/// Abre o desinstalador do fornecedor com <see cref="UninstallLauncher.Start"/> (ShellExecute: o Windows mostra o aviso de
/// administrador quando o desinstalador pede). Só espera o processo; nunca o fecha.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUninstallRunner : IUninstallRunner
{
    private const int ErrorCancelled = 1223;

    public IRunningUninstaller Start(UninstallCommand command)
    {
        try
        {
            return new Running(UninstallLauncher.Start(command));
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            throw new UninstallStartException("Aviso do Windows recusado.", cancelledByUser: true, e);
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            throw new UninstallStartException(e.Message, cancelledByUser: false, e);
        }
    }

    private sealed class Running(Process? process) : IRunningUninstaller
    {
        public int? ExitCode
        {
            get
            {
                if (process is null) return null;
                try { return process.HasExited ? process.ExitCode : null; }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
            }
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (process is null) return; // ShellExecute sem processo (ex.: relançado pelo shell): o registro decide
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        }

        public void Dispose() => process?.Dispose();
    }
}

/// <summary>
/// "Reinício pendente": as marcas documentadas que o Windows usa — <c>Component Based Servicing\RebootPending</c>,
/// <c>WindowsUpdate\Auto Update\RebootRequired</c> e <c>Session Manager\PendingFileRenameOperations</c> (arquivos que só podem ser
/// trocados no próximo boot, comum em desinstaladores que removem DLL em uso). A impressão digital é comparada antes/depois do
/// lote; uma marca que já existia antes não é atribuída aos desinstaladores.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRebootPendingProbe : IRebootPendingProbe
{
    public string? Fingerprint()
    {
        try
        {
            var sb = new StringBuilder();
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                sb.Append("cbs=").Append(k is not null).Append(';');
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                sb.Append("wu=").Append(k is not null).Append(';');
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            {
                var ops = k?.GetValue("PendingFileRenameOperations") as string[] ?? [];
                sb.Append("pfro=").Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ops)))));
            }
            return sb.ToString();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}

/// <summary>Releitura das chaves Uninstall para saber se um programa saiu.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsInstalledCheck
{
    public static bool StillInstalled(InstalledProgram program) =>
        ProgramInventory.Contains(ProgramInventory.Build(new WindowsUninstallRegistrySource().ReadAll()), program);
}
