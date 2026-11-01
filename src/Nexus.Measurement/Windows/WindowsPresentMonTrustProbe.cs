using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Nexus.Measurement.Windows;

/// <summary>
/// Conferência Authenticode do Windows: <c>WinVerifyTrust</c> (WINTRUST_ACTION_GENERIC_VERIFY_V2, sem interface, sem baixar
/// lista de revogação: só o cache local, regra "sem rede de terceiros") e o nome simples do certificado do assinante principal
/// (WTHelperProvDataFromStateData → WTHelperGetProvSignerFromChain → WTHelperGetProvCertFromChain).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPresentMonTrustProbe : IPresentMonTrustProbe
{
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public string? Sha256(string path) => PresentMonTrust.FileSha256(path);

    public PresentMonSignature Signature(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
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
                dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            var action = GenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            try
            {
                if (status != 0)
                    return PresentMonSignature.Invalid(status switch
                    {
                        unchecked((int)0x800B0100) => "o arquivo não tem assinatura digital",
                        unchecked((int)0x80096010) => "a assinatura não confere com o conteúdo (arquivo alterado)",
                        unchecked((int)0x800B0109) => "a cadeia do certificado não termina numa raiz confiável",
                        unchecked((int)0x800B0101) => "certificado vencido",
                        unchecked((int)0x800B010C) => "certificado revogado",
                        _ => $"WinVerifyTrust 0x{status:X8}",
                    });
                return new PresentMonSignature(true, SignerName(data.hWVTStateData), "assinatura válida");
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
