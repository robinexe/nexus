using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Lab.Gpu.Windows;

/// <summary>
/// Banco de perfis do driver NVIDIA (DRS) via NVAPI, sem SDK nem pacote: <c>nvapi64.dll</c> (instalada pelo driver em
/// System32, carregada SÓ desse caminho fixo) exporta <c>nvapi_QueryInterface(id)</c>, que devolve cada função pelo id
/// público de <c>nvapi_interface.h</c> (NVIDIA/nvapi). Estruturas montadas byte a byte com os deslocamentos de
/// <c>nvapi.h</c> (NVDRS_SETTING_V1 com #pragma pack(4) = 12320 bytes, versão 0x13020; NVDRS_APPLICATION_V4 = 20492 bytes,
/// 0x4500C, com recuo para V3 = 16396 bytes, 0x3400C; NVDRS_PROFILE_V1 = 4116 bytes, 0x11014), conferidos compilando os
/// typedefs do cabeçalho oficial.
/// <para>Carrega na primeira chamada; sem DLL, processo 32 bits ou NvAPI_Initialize falhando → <see cref="Unavailable"/>
/// diz o motivo e nada mais é chamado. Cada operação abre uma sessão DRS, carrega, (grava e salva) e destrói a sessão.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsNvidiaDrs : INvidiaDrs
{
    /// <summary>Prefixo do perfil que o NEXUS cria para um executável sem perfil (o único tipo de perfil que ele apaga).</summary>
    public const string OwnProfilePrefix = "NEXUS - ";

    // Ids de nvapi_interface.h.
    private const uint IdInitialize = 0x0150E828;
    private const uint IdCreateSession = 0x0694D52E;
    private const uint IdDestroySession = 0xDAD9CFF8;
    private const uint IdLoadSettings = 0x375DBD6B;
    private const uint IdSaveSettings = 0xFCBC7E14;
    private const uint IdCreateProfile = 0xCC176068;
    private const uint IdDeleteProfile = 0x17093206;
    private const uint IdFindProfileByName = 0x7E4A9A0B;
    private const uint IdGetProfileInfo = 0x61CD6FD6;
    private const uint IdCreateApplication = 0x4347A9DE;
    private const uint IdFindApplicationByName = 0xEEE566B2;
    private const uint IdSetSetting = 0x577DD202;
    private const uint IdGetSetting = 0x73BF8338;
    private const uint IdDeleteProfileSetting = 0xE4A26362;
    private const uint IdGetBaseProfile = 0xDA8466A0;

    // Tamanhos/deslocamentos de nvapi.h (ver resumo da classe).
    private const int UnicodeBytes = 2048 * 2;
    private const int SettingSize = 12320;
    private const uint SettingVer = SettingSize | (1u << 16);
    private const int SettingOffId = 4100, SettingOffType = 4104, SettingOffLocation = 4108, SettingOffIsCurrentPredefined = 4112,
        SettingOffPredefined = 4120, SettingOffCurrent = 8220;
    private const int AppV4Size = 20492, AppV3Size = 16396;
    private const uint AppV4Ver = AppV4Size | (4u << 16), AppV3Ver = AppV3Size | (3u << 16);
    private const int AppOffName = 8;
    private const int ProfileSize = 4116;
    private const uint ProfileVer = ProfileSize | (1u << 16);
    private const int ProfileOffName = 4, ProfileOffNumApps = 4108, ProfileOffNumSettings = 4112;
    private const uint DwordType = 0;            // NVDRS_DWORD_TYPE
    private const uint CurrentProfileLocation = 0, BaseProfileLocation = 2;

    private static readonly object Gate = new();
    private static Api? _api;
    private static string? _unavailable;
    private static bool _tried;

    private sealed class Api
    {
        public delegate* unmanaged[Cdecl]<IntPtr*, int> CreateSession;
        public delegate* unmanaged[Cdecl]<IntPtr, int> DestroySession, LoadSettings, SaveSettings;
        public delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int> CreateProfile;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> DeleteProfile;
        public delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int> FindProfileByName;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int> GetProfileInfo, CreateApplication, SetSetting;
        public delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, byte*, int> FindApplicationByName;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, byte*, int> GetSetting;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int> DeleteProfileSetting;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int> GetBaseProfile;
    }

    /// <summary>Caminho fixo da DLL (System32 do Windows; o driver NVIDIA a instala ali).</summary>
    public static string DllPath => Path.Combine(Environment.SystemDirectory, "nvapi64.dll");

    public string? Unavailable()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _unavailable;
        }
    }

    private static void EnsureLoaded()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            if (!Environment.Is64BitProcess) { _unavailable = "Sem placa/driver NVIDIA acessível: o NEXUS só usa a NVAPI em processo de 64 bits."; return; }
            var path = DllPath;
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var lib)) { _unavailable = "Sem placa/driver NVIDIA (nvapi64.dll não está no System32)."; return; }
            if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qiPtr)) { _unavailable = "Sem placa/driver NVIDIA (nvapi64.dll sem nvapi_QueryInterface)."; return; }
            var qi = (delegate* unmanaged[Cdecl]<uint, IntPtr>)qiPtr;
            IntPtr F(uint id)
            {
                var p = qi(id);
                return p != IntPtr.Zero ? p : throw new NvidiaDrsException($"Driver NVIDIA sem a função 0x{id:X8} da NVAPI (driver antigo?).");
            }
            var init = (delegate* unmanaged[Cdecl]<int>)F(IdInitialize);
            var st = init();
            if (st != NvApiStatus.Ok) { _unavailable = $"Sem placa/driver NVIDIA ativo (NvAPI_Initialize: {NvApiStatus.Describe(st)})."; return; }
            _api = new Api
            {
                CreateSession = (delegate* unmanaged[Cdecl]<IntPtr*, int>)F(IdCreateSession),
                DestroySession = (delegate* unmanaged[Cdecl]<IntPtr, int>)F(IdDestroySession),
                LoadSettings = (delegate* unmanaged[Cdecl]<IntPtr, int>)F(IdLoadSettings),
                SaveSettings = (delegate* unmanaged[Cdecl]<IntPtr, int>)F(IdSaveSettings),
                CreateProfile = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)F(IdCreateProfile),
                DeleteProfile = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)F(IdDeleteProfile),
                FindProfileByName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)F(IdFindProfileByName),
                GetProfileInfo = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)F(IdGetProfileInfo),
                CreateApplication = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)F(IdCreateApplication),
                FindApplicationByName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, byte*, int>)F(IdFindApplicationByName),
                SetSetting = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)F(IdSetSetting),
                GetSetting = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, byte*, int>)F(IdGetSetting),
                DeleteProfileSetting = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)F(IdDeleteProfileSetting),
                GetBaseProfile = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int>)F(IdGetBaseProfile),
            };
        }
        catch (Exception ex) when (ex is NvidiaDrsException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or SEHException)
        {
            _api = null;
            _unavailable = $"Sem placa/driver NVIDIA utilizável: {ex.Message}";
        }
    }

    private static Api Require()
    {
        EnsureLoaded();
        return _api ?? throw new NvidiaDrsException(_unavailable ?? "NVAPI indisponível.");
    }

    // ------------------------------------------------------------------ sessão

    private readonly struct Session : IDisposable
    {
        private readonly Api _api;
        public IntPtr Handle { get; }

        public Session(Api api)
        {
            _api = api;
            IntPtr h;
            Check("NvAPI_DRS_CreateSession", api.CreateSession(&h));
            Handle = h;
            var st = api.LoadSettings(h);
            if (st != NvApiStatus.Ok)
            {
                api.DestroySession(h);
                throw new NvidiaDrsException("NvAPI_DRS_LoadSettings", st);
            }
        }

        public void Save() => Check("NvAPI_DRS_SaveSettings", _api.SaveSettings(Handle));

        public void Dispose() => _api.DestroySession(Handle);
    }

    private static void Check(string op, int status)
    {
        if (status != NvApiStatus.Ok) throw new NvidiaDrsException(op, status);
    }

    private static byte* Alloc(int size) => (byte*)NativeMemory.AllocZeroed((nuint)size);

    private static void WriteUnicode(byte* dst, string s)
    {
        var n = Math.Min(s.Length, 2047);
        for (var i = 0; i < n; i++) ((char*)dst)[i] = s[i];
        ((char*)dst)[n] = '\0';
    }

    private static string ReadUnicode(byte* src)
    {
        var p = (char*)src;
        var n = 0;
        while (n < 2048 && p[n] != '\0') n++;
        return new string(p, 0, n);
    }

    /// <summary>Perfil do alvo. App sem perfil: <see cref="IntPtr.Zero"/> (ou cria, com <paramref name="create"/>).</summary>
    private static IntPtr Profile(Api api, IntPtr session, DrsTarget target, bool create, out string? name)
    {
        name = null;
        IntPtr h;
        if (target.Kind == DrsProfileKind.Base)
        {
            Check("NvAPI_DRS_GetBaseProfile", api.GetBaseProfile(session, &h));
            name = ProfileName(api, session, h);
            return h;
        }
        var exe = target.Exe!;
        var appName = Alloc(UnicodeBytes);
        var app = Alloc(AppV4Size);
        try
        {
            WriteUnicode(appName, exe);
            *(uint*)app = AppV4Ver;
            var st = api.FindApplicationByName(session, appName, &h, app);
            if (st == NvApiStatus.IncompatibleStructVersion)
            {
                NativeMemory.Clear(app, AppV4Size);
                *(uint*)app = AppV3Ver;
                st = api.FindApplicationByName(session, appName, &h, app);
            }
            if (st == NvApiStatus.Ok)
            {
                name = ProfileName(api, session, h);
                return h;
            }
            if (st is not (NvApiStatus.ExecutableNotFound or NvApiStatus.ProfileNotFound or NvApiStatus.SettingNotFound))
                throw new NvidiaDrsException($"NvAPI_DRS_FindApplicationByName({exe})", st);
            if (!create) return IntPtr.Zero;
            return CreateOwnProfile(api, session, exe, out name);
        }
        finally
        {
            NativeMemory.Free(appName);
            NativeMemory.Free(app);
        }
    }

    private static IntPtr CreateOwnProfile(Api api, IntPtr session, string exe, out string name)
    {
        name = OwnProfilePrefix + exe;
        var profile = Alloc(ProfileSize);
        var nameBuf = Alloc(UnicodeBytes);
        var app = Alloc(AppV4Size);
        try
        {
            IntPtr h;
            WriteUnicode(nameBuf, name);
            var st = api.FindProfileByName(session, nameBuf, &h);
            if (st != NvApiStatus.Ok)
            {
                *(uint*)profile = ProfileVer;
                WriteUnicode(profile + ProfileOffName, name);
                Check("NvAPI_DRS_CreateProfile", api.CreateProfile(session, profile, &h));
            }
            *(uint*)app = AppV4Ver;
            WriteUnicode(app + AppOffName, exe);
            st = api.CreateApplication(session, h, app);
            if (st == NvApiStatus.IncompatibleStructVersion)
            {
                NativeMemory.Clear(app, AppV4Size);
                *(uint*)app = AppV3Ver;
                WriteUnicode(app + AppOffName, exe);
                st = api.CreateApplication(session, h, app);
            }
            Check($"NvAPI_DRS_CreateApplication({exe})", st);
            return h;
        }
        finally
        {
            NativeMemory.Free(profile);
            NativeMemory.Free(nameBuf);
            NativeMemory.Free(app);
        }
    }

    private static string? ProfileName(Api api, IntPtr session, IntPtr profile) =>
        ProfileInfo(api, session, profile, out var name, out _, out _) ? name : null;

    private static bool ProfileInfo(Api api, IntPtr session, IntPtr profile, out string name, out uint apps, out uint settings)
    {
        var buf = Alloc(ProfileSize);
        try
        {
            *(uint*)buf = ProfileVer;
            var ok = api.GetProfileInfo(session, profile, buf) == NvApiStatus.Ok;
            name = ok ? ReadUnicode(buf + ProfileOffName) : "";
            apps = ok ? *(uint*)(buf + ProfileOffNumApps) : 0;
            settings = ok ? *(uint*)(buf + ProfileOffNumSettings) : 0;
            return ok;
        }
        finally { NativeMemory.Free(buf); }
    }

    private static DrsReading ReadIn(Api api, IntPtr session, IntPtr profile, string? name, DrsTarget target, uint settingId)
    {
        var s = Alloc(SettingSize);
        try
        {
            *(uint*)s = SettingVer;
            var st = api.GetSetting(session, profile, settingId, s);
            if (st == NvApiStatus.SettingNotFound) return new DrsReading(true, name, false, null, false, null);
            Check("NvAPI_DRS_GetSetting", st);
            if (*(uint*)(s + SettingOffType) != DwordType) throw new NvidiaDrsException($"Ajuste {NvidiaDrsSettings.Hex(settingId)} não é DWORD neste driver.");
            var location = *(uint*)(s + SettingOffLocation);
            var value = *(uint*)(s + SettingOffCurrent);
            var predefined = *(uint*)(s + SettingOffIsCurrentPredefined) != 0;
            var inProfile = location == CurrentProfileLocation || (target.Kind == DrsProfileKind.Base && location == BaseProfileLocation);
            return new DrsReading(true, name, inProfile, inProfile ? value : null, inProfile && predefined, value);
        }
        finally { NativeMemory.Free(s); }
    }

    // ------------------------------------------------------------------ INvidiaDrs

    public DrsReading Read(DrsTarget target, uint settingId)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (Gate)
        {
            var api = Require();
            using var session = new Session(api);
            var h = Profile(api, session.Handle, target, create: false, out var name);
            return h == IntPtr.Zero ? DrsReading.NoProfile : ReadIn(api, session.Handle, h, name, target, settingId);
        }
    }

    public void SetUserValue(DrsTarget target, uint settingId, uint value)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (Gate)
        {
            var api = Require();
            using var session = new Session(api);
            var h = Profile(api, session.Handle, target, create: true, out _);
            var s = Alloc(SettingSize);
            try
            {
                *(uint*)s = SettingVer;
                *(uint*)(s + SettingOffId) = settingId;
                *(uint*)(s + SettingOffType) = DwordType;
                *(uint*)(s + SettingOffLocation) = CurrentProfileLocation;
                *(uint*)(s + SettingOffPredefined) = value;
                *(uint*)(s + SettingOffCurrent) = value;
                Check("NvAPI_DRS_SetSetting", api.SetSetting(session.Handle, h, s));
            }
            finally { NativeMemory.Free(s); }
            session.Save();
        }
    }

    public void ClearUserValue(DrsTarget target, uint settingId)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (Gate)
        {
            var api = Require();
            using var session = new Session(api);
            var h = Profile(api, session.Handle, target, create: false, out var name);
            if (h == IntPtr.Zero) return;
            var st = api.DeleteProfileSetting(session.Handle, h, settingId);
            if (st is not (NvApiStatus.Ok or NvApiStatus.SettingNotFound)) throw new NvidiaDrsException("NvAPI_DRS_DeleteProfileSetting", st);
            // Perfil criado pelo NEXUS que ficou sem nenhum ajuste e só com o próprio executável: some junto.
            if (target.Kind == DrsProfileKind.App && name == OwnProfilePrefix + target.Exe &&
                ProfileInfo(api, session.Handle, h, out _, out var apps, out var settings) && settings == 0 && apps <= 1)
                Check("NvAPI_DRS_DeleteProfile", api.DeleteProfile(session.Handle, h));
            session.Save();
        }
    }
}
