using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Win32;
using Nexus.NetRoute.Handlers;
using Nexus.NetRoute.WireGuard;

namespace Nexus.NetRoute.Windows;

/// <summary>ACL dos arquivos da rota: segredo só para Administradores/SYSTEM; pasta com Usuários só leitura.</summary>
[SupportedOSPlatform("windows")]
public static class NetRouteAcl
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    public static void ProtectSecretFile(string path)
    {
        var fs = new FileSecurity();
        fs.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fs.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        fs.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        try { fs.SetOwner(Admins); } catch (InvalidOperationException) { }
        new FileInfo(path).SetAccessControl(fs);
    }

    public static void ProtectDirectory(string path)
    {
        var ds = new DirectorySecurity();
        ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        ds.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        try { ds.SetOwner(Admins); } catch (InvalidOperationException) { }
        new DirectoryInfo(path).SetAccessControl(ds);
    }
}

/// <summary>
/// Cliente WireGuard oficial e o serviço do túnel do NEXUS (<c>wireguard.exe /installtunnelservice</c>, serviço
/// <c>WireGuardTunnel$NexusRota</c>). Roda dentro do aplicador de administrador.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetRouteSystem(string directory) : INetRouteSystem
{
    public const string ServiceName = "WireGuardTunnel$" + TunnelConfig.TunnelName;
    private static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(20);

    public string ConfigPath => Path.Combine(directory, TunnelConfig.TunnelName + ".conf");

    public static string WireGuardExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe");

    public bool ClientInstalled => File.Exists(WireGuardExe) && SignedByWireGuard(WireGuardExe);

    /// <summary>Assinatura Authenticode válida E emitida para a WireGuard LLC (não basta estar assinado por qualquer um).</summary>
    public static bool SignedByWireGuard(string path)
    {
        if (Nexus.Security.Authenticode.IsSigned(path) != true) return false;
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile: não há substituto para ler o certificado do assinante
            var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains("O=" + WireGuardRelease.Publisher, StringComparison.Ordinal) ||
                   cert.Subject.Contains("CN=" + WireGuardRelease.Publisher, StringComparison.Ordinal);
        }
        catch (CryptographicException) { return false; }
    }

    public void InstallClient()
    {
        var msi = WireGuardDownloader.DownloadVerified(Path.Combine(Path.GetTempPath(), "nexus-wireguard-" + Guid.NewGuid().ToString("N")));
        try
        {
            var (code, output) = HiddenProcess.Run("msiexec.exe", ["/i", msi, "/qn", "/norestart", "DO_NOT_LAUNCH=1"], TimeSpan.FromMinutes(5));
            if (code is not (0 or 3010))
                throw new InvalidOperationException($"O instalador do WireGuard terminou com código {code}. {output}".Trim());
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(msi)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (!ClientInstalled) throw new InvalidOperationException("O WireGuard não ficou instalado (ou a assinatura não confere).");
    }

    public void UninstallClient()
    {
        var code = ProductCode() ?? throw new InvalidOperationException("Não achei o WireGuard na lista de programas instalados.");
        var (exit, output) = HiddenProcess.Run("msiexec.exe", ["/x", code, "/qn", "/norestart"], TimeSpan.FromMinutes(5));
        if (exit is not (0 or 3010)) throw new InvalidOperationException($"A desinstalação do WireGuard terminou com código {exit}. {output}".Trim());
    }

    /// <summary>Código do produto MSI do WireGuard oficial (Uninstall\{GUID} com editor WireGuard LLC).</summary>
    private static string? ProductCode()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (root is null) return null;
        foreach (var name in root.GetSubKeyNames())
        {
            if (!Guid.TryParse(name, out _)) continue;
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("DisplayName") is string dn && dn.StartsWith("WireGuard", StringComparison.Ordinal) &&
                k.GetValue("Publisher") is string pub && pub.Contains(WireGuardRelease.Publisher, StringComparison.Ordinal))
                return name;
        }
        return null;
    }

    public bool TunnelInstalled => ServiceControl.State(ServiceName) is not null;

    public string? ReadTunnelConfig() => File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;

    public void InstallTunnel(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);
        if (!ClientInstalled) throw new InvalidOperationException("Cliente WireGuard oficial ausente.");
        Directory.CreateDirectory(directory);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, "");
        NetRouteAcl.ProtectSecretFile(tmp);
        File.WriteAllText(tmp, configText);
        File.Move(tmp, ConfigPath, overwrite: true);
        var (code, output) = HiddenProcess.Run(WireGuardExe, ["/installtunnelservice", ConfigPath], TimeSpan.FromSeconds(60));
        if (code != 0) throw new InvalidOperationException($"O WireGuard recusou ligar o túnel (código {code}). {output}".Trim());
        if (!WaitFor(() => ServiceControl.State(ServiceName) == ServiceControl.Running))
        {
            _ = HiddenProcess.Run(WireGuardExe, ["/uninstalltunnelservice", TunnelConfig.TunnelName], TimeSpan.FromSeconds(60));
            throw new InvalidOperationException("O túnel não entrou em funcionamento em 20 s (relay inacessível ou porta UDP bloqueada?). Nada ficou ligado.");
        }
    }

    public void RemoveTunnel()
    {
        if (ServiceControl.State(ServiceName) is not null)
        {
            var (code, output) = HiddenProcess.Run(WireGuardExe, ["/uninstalltunnelservice", TunnelConfig.TunnelName], TimeSpan.FromSeconds(60));
            if (code != 0 && ServiceControl.State(ServiceName) is not null)
                throw new InvalidOperationException($"O WireGuard não desligou o túnel (código {code}). {output}".Trim());
            if (!WaitFor(() => ServiceControl.State(ServiceName) is null))
                throw new InvalidOperationException("O serviço do túnel ainda existe depois de 20 s; tente de novo.");
        }
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < ServiceWait)
        {
            if (condition()) return true;
            Thread.Sleep(250);
        }
        return condition();
    }
}

/// <summary>Baixa o MSI oficial e confere o BLAKE2b-256 (lista da WireGuard LLC) e a assinatura Authenticode "WireGuard LLC".</summary>
[SupportedOSPlatform("windows")]
public static class WireGuardDownloader
{
    public static string DownloadVerified(string folder)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NEXUS-netroute/1.0");
        var list = http.GetStringAsync(new Uri(WireGuardRelease.BaseUrl + WireGuardRelease.ListFile)).GetAwaiter().GetResult();
        var arch = WireGuardRelease.ArchitectureOf(RuntimeInformation.OSArchitecture);
        var msi = WireGuardRelease.Pick(WireGuardRelease.ParseList(list), arch)
                  ?? throw new InvalidOperationException($"A lista oficial do WireGuard não tem instalador para {arch}.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, msi.FileName);
        var bytes = http.GetByteArrayAsync(msi.Url).GetAwaiter().GetResult();
        if (!string.Equals(Blake2b.HashHex(bytes), msi.Blake2b256, StringComparison.Ordinal))
            throw new InvalidOperationException("O instalador baixado não confere com o hash (BLAKE2b-256) publicado pela WireGuard LLC. Nada foi instalado.");
        File.WriteAllBytes(path, bytes);
        if (!WindowsNetRouteSystem.SignedByWireGuard(path))
        {
            File.Delete(path);
            throw new InvalidOperationException("O instalador baixado não tem assinatura válida da WireGuard LLC. Nada foi instalado.");
        }
        return path;
    }
}

/// <summary>Processo sem janela, com prazo e saída capturada (para mensagens de erro).</summary>
[SupportedOSPlatform("windows")]
internal static class HiddenProcess
{
    public static (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {Path.GetFileName(file)}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(file)} não terminou em {timeout.TotalSeconds:0} s.");
        }
        var text = (stdout.GetAwaiter().GetResult() + " " + stderr.GetAwaiter().GetResult()).Trim();
        return (p.ExitCode, text.Length > 400 ? text[..400] : text);
    }
}

/// <summary>Estado de um serviço do Windows sem o pacote ServiceController (OpenSCManager/QueryServiceStatus).</summary>
[SupportedOSPlatform("windows")]
internal static partial class ServiceControl
{
    public const uint Running = 4;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenService(IntPtr manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    /// <summary>Estado atual (4 = em execução) ou null se o serviço não existe.</summary>
    public static uint? State(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var svc = OpenService(scm, serviceName, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return null;
            try { return QueryServiceStatus(svc, out var st) ? st.CurrentState : 0; }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
