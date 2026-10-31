using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.Cleaning;

/// <summary>
/// Pastas reais no Windows. Para um SID (aplicador como serviço), tudo vem do registro do perfil daquele usuário
/// (só ProfileList, em HKLM) e nunca do processo do aplicador. Só leitura.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsCleaningFolders
{
    /// <summary>Pastas da própria conta do processo (interface ou aplicador com a mesma conta).</summary>
    public static CleaningFolders ForCurrentProcess(IRegistryBackend registry)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ForMachine(registry) with
        {
            UserProfile = Nullify(profile),
            LocalAppData = Nullify(local),
            LocalAppDataLow = string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData", "LocalLow"),
            AppData = Nullify(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            UserTemp = Nullify(Path.TrimEndingDirectorySeparator(Path.GetTempPath())),
        };
    }

    /// <summary>
    /// Pastas do perfil de um SID. Null se o perfil não puder ser identificado com segurança.
    /// <para>Só a pasta do perfil vem do registro, e de <c>HKLM\...\ProfileList</c> (que o usuário não altera). As demais são
    /// derivadas dela (<c>AppData\Local</c>, <c>AppData\Roaming</c>, <c>AppData\Local\Temp</c>). Antes vinham de
    /// <c>HKU\&lt;SID&gt;\Volatile Environment</c> e <c>HKU\&lt;SID&gt;\Environment\TEMP</c>, que o próprio usuário escreve: um usuário
    /// comum punha <c>TEMP=C:\Windows\Installer</c> (ou a pasta de outro programa ou de outra conta) e o aplicador, como
    /// SYSTEM, levava esses arquivos para a quarentena.</para>
    /// </summary>
    public static CleaningFolders? ForSid(IRegistryBackend registry, string sid)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!UserProfileScope.IsValidSid(sid)) return null;
        var profile = ProfileImagePath(registry, sid);
        if (profile is null || !Path.IsPathFullyQualified(profile) || profile.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        profile = Path.TrimEndingDirectorySeparator(profile);
        var local = Path.Combine(profile, "AppData", "Local");
        return ForMachine(registry) with
        {
            UserProfile = profile,
            LocalAppData = local,
            AppData = Path.Combine(profile, "AppData", "Roaming"),
            LocalAppDataLow = Path.Combine(profile, "AppData", "LocalLow"),
            UserTemp = Path.Combine(local, "Temp"),
        };
    }

    /// <summary>Só as pastas da máquina (perfil vazio): para regras de máquina no aplicador que roda com outra conta.</summary>
    public static CleaningFolders ForMachine(IRegistryBackend registry) => new(
        null, null, null, null, null,
        Nullify(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
        Nullify(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
        SteamPath(registry));

    private static string? ProfileImagePath(IRegistryBackend registry, string sid)
    {
        try
        {
            var r = registry.Read(new RegistryTarget(RegHive.HKLM, $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}", "ProfileImagePath", RegView.Registry64));
            if (!r.ValueExists || r.Value is null) return null;
            var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var v = r.Value.Replace("%SystemDrive%", drive, StringComparison.OrdinalIgnoreCase);
            return v.Contains('%', StringComparison.Ordinal) ? null : v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { return null; }
    }

    /// <summary>Pasta do Steam pela instalação registrada (HKLM, visão 32 bits); senão o local padrão, se existir.</summary>
    public static string? SteamPath(IRegistryBackend registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        try
        {
            var r = registry.Read(new RegistryTarget(RegHive.HKLM, @"SOFTWARE\Valve\Steam", "InstallPath", RegView.Registry32));
            if (r.ValueExists && r.Value is { } p && Path.IsPathFullyQualified(p) && !p.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(p))
                return Path.TrimEndingDirectorySeparator(p);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Tamanho da Lixeira (todas as unidades), pela API do Shell. Só leitura; esvaziar fica com o Windows.</summary>
[SupportedOSPlatform("windows")]
public static partial class RecycleBinInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo64
    {
        public int CbSize;
        public long I64Size;
        public long I64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ShQueryRbInfo32
    {
        public int CbSize;
        public long I64Size;
        public long I64NumItems;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Query64(string? rootPath, ref ShQueryRbInfo64 info);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Query32(string? rootPath, ref ShQueryRbInfo32 info);

    public static (long Files, long Bytes)? TryQuery()
    {
        try
        {
            if (IntPtr.Size == 8)
            {
                var info = new ShQueryRbInfo64 { CbSize = Marshal.SizeOf<ShQueryRbInfo64>() };
                return Query64(null, ref info) == 0 ? (info.I64NumItems, info.I64Size) : null;
            }
            var info32 = new ShQueryRbInfo32 { CbSize = Marshal.SizeOf<ShQueryRbInfo32>() };
            return Query32(null, ref info32) == 0 ? (info32.I64NumItems, info32.I64Size) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return null; }
    }
}
