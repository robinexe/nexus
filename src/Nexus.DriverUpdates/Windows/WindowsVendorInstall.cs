using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Nexus.Core;
using Nexus.DriverUpdates.Vendor;

namespace Nexus.DriverUpdates.Windows;

/// <summary>
/// Authenticode do Windows: <c>WinVerifyTrust</c> (WINTRUST_ACTION_GENERIC_VERIFY_V2, sem interface; revogação só pelo cache local,
/// sem baixar listas) e o nome simples do certificado do assinante principal (WTHelperProvDataFromStateData →
/// WTHelperGetProvSignerFromChain → WTHelperGetProvCertFromChain). Mesmo padrão de Nexus.Measurement (PresentMon).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsAuthenticodeVerifier : ISignatureVerifier
{
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public SignatureCheck Verify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) return new(false, null, "arquivo não encontrado");
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = pathPtr };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = filePtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            var action = GenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            try
            {
                if (status != 0)
                    return new(false, null, status switch
                    {
                        unchecked((int)0x800B0100) => "o arquivo não tem assinatura digital",
                        unchecked((int)0x80096010) => "a assinatura não confere com o conteúdo (arquivo alterado)",
                        unchecked((int)0x800B0109) => "a cadeia do certificado não termina numa raiz confiável",
                        unchecked((int)0x800B0101) => "certificado vencido",
                        unchecked((int)0x800B010C) => "certificado revogado",
                        _ => $"WinVerifyTrust 0x{status:X8}",
                    });
                var signer = SignerName(data.hWVTStateData);
                return new(true, signer, "assinatura válida");
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    private static string? SignerName(IntPtr state)
    {
        if (state == IntPtr.Zero) return null;
        var provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero) return null;
        var signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
        if (signer == IntPtr.Zero) return null;
        var providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero) return null;
        // CRYPT_PROVIDER_CERT: DWORD cbStruct; (alinhamento) PCCERT_CONTEXT pCert; ...
        var certContext = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
        if (certContext == IntPtr.Zero) return null;
        using var cert = new X509Certificate2(certContext); // duplica o contexto; o original é liberado no CLOSE
        return cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvSignerFromChain(IntPtr providerData, uint signerIndex, int counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);
}

/// <summary>Roda o instalador (sem shell, argumentos fixos) e espera até o tempo limite. Estourou: NÃO mata (driver pela metade é pior).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInstallerRunner : IInstallerRunner
{
    public InstallerRunResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o instalador.");
        if (!p.WaitForExit(timeout)) return new InstallerRunResult(null, true);
        return new InstallerRunResult(p.ExitCode, false);
    }
}

/// <summary>Perfil pelo SID (HKLM\...\ProfileList, que o usuário não altera) ou o do próprio processo (aplicador na conta de quem pede).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUserProfileLocator(bool otherAccount) : IUserProfileLocator
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public string? UserProfile(string? userSid)
    {
        if (userSid is not null)
        {
            if (!UserProfileScope.IsValidSid(userSid)) return null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{ProfileList}\{userSid}");
                var path = key?.GetValue("ProfileImagePath") as string;
                return string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        }
        return otherAccount ? null : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}

/// <summary>
/// Exceção controlada (brief, regra 2): o "Auto-Detect and Install" da AMD, baixado do servidor oficial e com a assinatura da AMD
/// conferida, é ABERTO para o usuário VER e controlar (janela do próprio instalador da AMD). Só depois de Host.Confirm.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AmdAutoDetectTool
{
    public static void Launch(string path, ISignatureVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var sig = verifier.Verify(path);
        if (!sig.Valid || !VendorDriverPolicy.IsAllowedSigner(Nexus.Lab.Gpu.GpuVendor.Amd, sig.Signer))
            throw new InvalidOperationException($"Assinatura da AMD não confere ({sig.Reason}; assinante: {sig.Signer ?? "nenhum"}): o programa NÃO foi aberto.");
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
    }
}
