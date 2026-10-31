using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Nexus.Boot.Inventory;
using Nexus.WindowsActions.Registry;

namespace Nexus.Boot.Windows;

/// <summary>Enumeração de valores do registro (somente leitura).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBootRegistryReader : IBootRegistryReader
{
    public IReadOnlyList<RegValue> Values(RegHive hive, string path, RegView view)
    {
        try
        {
            var v = view switch { RegView.Registry32 => RegistryView.Registry32, RegView.Registry64 => RegistryView.Registry64, _ => RegistryView.Default };
            using var root = RegistryKey.OpenBaseKey(hive == RegHive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, v);
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null) return [];
            var list = new List<RegValue>();
            foreach (var name in key.GetValueNames())
            {
                try
                {
                    var kind = key.GetValueKind(name);
                    var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    var data = kind switch
                    {
                        RegistryValueKind.Binary => raw is byte[] b ? Convert.ToHexStringLower(b) : "",
                        RegistryValueKind.MultiString => raw is string[] a ? string.Join('\u0000', a) : "",
                        RegistryValueKind.DWord => raw is int i ? unchecked((uint)i).ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                        _ => raw?.ToString() ?? "",
                    };
                    list.Add(new RegValue(name, kind.ToString(), data));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }
}

/// <summary>Arquivos: existência, listagem rasa (sem seguir links), editor (Authenticode sem rede, senão versão do arquivo).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsFileInspector : IFileInspector
{
    private readonly Dictionary<string, FilePublisher> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public IReadOnlyList<string> Files(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateFiles(folder, "*", options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public byte[]? ReadSmall(string path, int maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public FilePublisher Publisher(string path)
    {
        lock (_cache)
            if (_cache.TryGetValue(path, out var cached)) return cached;
        var result = Read(path);
        lock (_cache) _cache[path] = result;
        return result;
    }

    private static FilePublisher Read(string path)
    {
        if (!File.Exists(path)) return FilePublisher.Unknown;
        var trust = VerifyEmbedded(path);
        if (trust != TrustNoSignature)
        {
            var signer = SignerName(path);
            if (trust == 0) return new FilePublisher(signer, PublisherSource.Signature);
            if (signer is not null) return new FilePublisher(signer, PublisherSource.SignatureNotTrusted);
        }
        try
        {
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
            return string.IsNullOrEmpty(company) ? FilePublisher.Unknown : new FilePublisher(company, PublisherSource.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return FilePublisher.Unknown; }
    }

    private static string? SignerName(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Não há substituto para extrair o certificado do signatário de um PE assinado; só lemos o nome.
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return NameFromSubject(cert.Subject);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"CN=Valve Corp., O=Valve Corp., L=..." → "Valve Corp.".</summary>
    internal static string? NameFromSubject(string subject)
    {
        foreach (var key in new[] { "CN=", "O=" })
        {
            var i = subject.IndexOf(key, StringComparison.Ordinal);
            if (i < 0 || (i > 0 && subject[i - 1] is not (' ' or ','))) continue;
            var start = i + key.Length;
            if (start < subject.Length && subject[start] == '"')
            {
                var end = subject.IndexOf('"', start + 1);
                return end < 0 ? subject[(start + 1)..] : subject[(start + 1)..end];
            }
            var comma = subject.IndexOf(',', start);
            return (comma < 0 ? subject[start..] : subject[start..comma]).Trim();
        }
        return null;
    }

    // ---- WinVerifyTrust: só a assinatura embutida, sem interface e sem buscar nada na rede.
    private const int TrustNoSignature = unchecked((int)0x800B0100);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    private static unsafe int VerifyEmbedded(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        try
        {
            var file = new WinTrustFileInfo { StructSize = (uint)sizeof(WinTrustFileInfo), FilePath = pathPtr };
            var data = new WinTrustData
            {
                StructSize = (uint)sizeof(WinTrustData),
                UiChoice = 2,          // WTD_UI_NONE
                RevocationChecks = 0,  // WTD_REVOKE_NONE
                UnionChoice = 1,       // WTD_CHOICE_FILE
                FileInfo = (IntPtr)(&file),
                StateAction = 0,       // WTD_STATEACTION_IGNORE
                ProvFlags = 0x1000 | 0x10, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE: nada de rede
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data); // INVALID_HANDLE_VALUE: sem janela
        }
        finally { Marshal.FreeHGlobal(pathPtr); }
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}

/// <summary>Pastas Inicializar do usuário atual e comum.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsStartupFolders
{
    public static StartupFolders Current()
    {
        static string? Get(Environment.SpecialFolder f)
        {
            var p = Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
            return string.IsNullOrEmpty(p) ? null : p;
        }
        return new StartupFolders(Get(Environment.SpecialFolder.Startup), Get(Environment.SpecialFolder.CommonStartup));
    }
}
