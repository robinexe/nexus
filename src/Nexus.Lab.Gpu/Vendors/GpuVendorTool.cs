using System.Runtime.InteropServices;

namespace Nexus.Lab.Gpu.Vendors;

/// <summary>
/// Ajustes do driver AMD (ADLX, amdadlx64.dll) e Intel (IGCL, ControlLib.dll) que valem para todos os jogos: lista FECHADA.
/// Roda SEMPRE num processo separado (<c>nexus-broker.exe --gpu-vendor-tool get|set ...</c>): código nativo do fabricante que
/// falhe derruba só esse processo, nunca o aplicador. Saída de uma linha: <c>ok:VALOR</c>, <c>unsupported:MOTIVO</c> ou
/// <c>error:MOTIVO</c>. As DLLs vêm com o driver de vídeo instalado; o NEXUS não as distribui (docs/05-dependencies.md).
/// Bindings escritos a partir dos cabeçalhos públicos (ADLX SDK 2.0, MIT; IGCL 1.1, MIT): ordem das funções das vtables e
/// layout das estruturas conferidos contra esses arquivos.
/// </summary>
public static class GpuVendorTool
{
    public const string Switch = "--gpu-vendor-tool";

    /// <summary>Ajustes aceitos: fornecedor/ajuste → valores que o NEXUS grava (o desfazer devolve o valor lido antes).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Settings { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["amd/antilag"] = ["on", "off"],
        ["amd/chill"] = ["on", "off"],
        ["amd/frtc"] = ["on", "off"],      // Frame Rate Target Control = limite de FPS do driver
        ["amd/vsync"] = ["0", "1", "2", "3"], // Wait for Vertical Refresh: 0 sempre desligado … 3 sempre ligado
        ["intel/lowlatency"] = ["0", "1", "2"],
        ["intel/endurance"] = ["0", "1", "2"], // Endurance Gaming: 0 desligado, 1 ligado, 2 automático
        ["intel/framelimit"] = ["on", "off"],
    };

    public static bool IsValid(string setting, string? value) =>
        Settings.TryGetValue(setting, out var allowed) && (value is null || allowed.Contains(value, StringComparer.Ordinal));

    /// <summary>Ponto de entrada do processo separado. args = ["get", "amd/antilag"] ou ["set", "amd/antilag", "on"].</summary>
    public static int Main(string[] args)
    {
        string line;
        try { line = Run(args); }
        catch (Exception ex) { line = "error:" + ex.GetType().Name + ": " + ex.Message; }
        Console.Out.WriteLine(line.ReplaceLineEndings(" "));
        return line.StartsWith("ok:", StringComparison.Ordinal) ? 0 : line.StartsWith("unsupported:", StringComparison.Ordinal) ? 3 : 1;
    }

    public static string Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!OperatingSystem.IsWindows()) return "unsupported:só no Windows";
        if (args.Length < 2 || args[0] is not ("get" or "set")) return "error:uso: get|set fornecedor/ajuste [valor]";
        var set = args[0] == "set";
        var setting = args[1];
        var value = set ? (args.Length == 3 ? args[2] : null) : null;
        if (!IsValid(setting, value) || (set && value is null)) return "error:ajuste ou valor fora da lista";
        return setting.StartsWith("amd/", StringComparison.Ordinal) ? Adlx.Run(setting[4..], value) : Igcl.Run(setting[6..], value);
    }
}

/// <summary>AMD ADLX (C, vtables __stdcall; inicialização __cdecl). Aplica em todas as GPUs AMD; lê da primeira que suporta.</summary>
internal static unsafe class Adlx
{
    private const int Ok = 0, AlreadyEnabled = 1, AlreadyInitialized = 2;
    private static bool Succeeded(int r) => r is Ok or AlreadyEnabled or AlreadyInitialized;
    private const ulong Version = (2UL << 48) | (0UL << 32) | (0UL << 16) | 125UL; // ADLX_FULL_VERSION do SDK 2.0.0.125

    private static IntPtr Vtbl(IntPtr obj) => *(IntPtr*)obj;
    private static IntPtr Fn(IntPtr obj, int index) => ((IntPtr*)Vtbl(obj))[index];
    private static void Release(IntPtr obj) { if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, long>)Fn(obj, 1))(obj); }

    public static string Run(string setting, string? value)
    {
        if (!NativeLibrary.TryLoad("amdadlx64.dll", out var lib)) return "unsupported:driver AMD (ADLX) não encontrado";
        if (!NativeLibrary.TryGetExport(lib, "ADLXInitialize", out var init)) return "unsupported:ADLX sem ADLXInitialize";
        NativeLibrary.TryGetExport(lib, "ADLXInitializeWithIncompatibleDriver", out var initIncompat);
        NativeLibrary.TryGetExport(lib, "ADLXTerminate", out var terminate);
        IntPtr system;
        var rc = ((delegate* unmanaged[Cdecl]<ulong, IntPtr*, int>)init)(Version, &system);
        if (!Succeeded(rc) && initIncompat != IntPtr.Zero) rc = ((delegate* unmanaged[Cdecl]<ulong, IntPtr*, int>)initIncompat)(Version, &system);
        if (!Succeeded(rc) || system == IntPtr.Zero) return $"unsupported:ADLX não iniciou (código {rc})";
        try { return WithSystem(system, setting, value); }
        finally { if (terminate != IntPtr.Zero) ((delegate* unmanaged[Cdecl]<int>)terminate)(); }
    }

    private static string WithSystem(IntPtr system, string setting, string? value)
    {
        // IADLXSystem (sem Acquire/Release): 1 GetGPUs, 7 Get3DSettingsServices
        IntPtr gpus, services;
        if (!Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Fn(system, 1))(system, &gpus)) || gpus == IntPtr.Zero)
            return "unsupported:nenhuma GPU AMD";
        if (!Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Fn(system, 7))(system, &services)) || services == IntPtr.Zero)
        {
            Release(gpus);
            return "unsupported:ADLX sem ajustes 3D";
        }
        try
        {
            // IADLXGPUList: 3 Size, 11 At_GPUList
            var count = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(gpus, 3))(gpus);
            string? read = null;
            for (uint i = 0; i < count; i++)
            {
                IntPtr gpu;
                if (!Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(gpus, 11))(gpus, i, &gpu)) || gpu == IntPtr.Zero) continue;
                try
                {
                    var r = OnGpu(services, gpu, setting, value);
                    if (r is null) continue;
                    read ??= r;
                }
                finally { Release(gpu); }
            }
            if (read is null) return "unsupported:nenhuma GPU AMD suporta este ajuste";
            return "ok:" + read;
        }
        finally
        {
            Release(services);
            Release(gpus);
        }
    }

    /// <summary>Lê (value null) ou grava o ajuste nesta GPU. Null = não suportado aqui. Retorna o valor lido ANTES de gravar.</summary>
    private static string? OnGpu(IntPtr services, IntPtr gpu, string setting, string? value)
    {
        // IADLX3DSettingsServices: 3 GetAntiLag, 4 GetChill, 8 GetWaitForVerticalRefresh, 9 GetFrameRateTargetControl
        var getter = setting switch { "antilag" => 3, "chill" => 4, "vsync" => 8, "frtc" => 9, _ => -1 };
        if (getter < 0) return null;
        IntPtr feature;
        if (!Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Fn(services, getter))(services, gpu, &feature)) || feature == IntPtr.Zero)
            return null;
        try
        {
            byte supported = 0, enabled = 0;
            if (!Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Fn(feature, 3))(feature, &supported)) || supported == 0) return null;
            if (setting == "vsync")
            {
                // IADLX3DWaitForVerticalRefresh: 5 GetMode(int*), 6 SetMode(int)
                int mode;
                if (!Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Fn(feature, 5))(feature, &mode))) return null;
                if (value is not null && !Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Fn(feature, 6))(feature, int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))))
                    throw new InvalidOperationException("ADLX recusou o modo de V-Sync.");
                return mode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Fn(feature, 4))(feature, &enabled))) return null;
            if (value is not null)
            {
                // SetEnabled: Anti-Lag 5, Chill 8, FRTC 7
                var setter = setting switch { "antilag" => 5, "chill" => 8, _ => 7 };
                if (!Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Fn(feature, setter))(feature, value == "on" ? (byte)1 : (byte)0)))
                    throw new InvalidOperationException("ADLX recusou o ajuste.");
            }
            return enabled != 0 ? "on" : "off";
        }
        finally { Release(feature); }
    }
}

/// <summary>Intel IGCL (ControlLib.dll, __cdecl). Ajustes globais (sem nome de aplicativo) em todos os adaptadores Intel.</summary>
internal static unsafe class Igcl
{
    private const int Success = 0;
    private const int FeatureEndurance = 1, FeatureFrameLimit = 2, FeatureLowLatency = 16;
    private const int TypeInt32 = 2, TypeEnum = 4, TypeCustom = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public Guid ApplicationUid;
    }

    /// <summary>ctl_3d_feature_getset_t (x64): 56 bytes; Value é a união ctl_property_t (8 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 56)]
    private struct FeatureGetSet
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public int FeatureType;
        [FieldOffset(16)] public IntPtr ApplicationName;
        [FieldOffset(24)] public sbyte ApplicationNameLength;
        [FieldOffset(25)] public byte Set;
        [FieldOffset(28)] public int ValueType;
        [FieldOffset(32)] public byte ValueBool;     // Enable de bool/int/uint
        [FieldOffset(32)] public uint ValueEnum;     // EnableType de enum
        [FieldOffset(36)] public int ValueInt;
        [FieldOffset(40)] public int CustomValueSize;
        [FieldOffset(48)] public IntPtr CustomValue;
    }

    public static string Run(string setting, string? value)
    {
        if (!NativeLibrary.TryLoad("ControlLib.dll", out var lib)) return "unsupported:driver Intel (IGCL) não encontrado";
        if (!NativeLibrary.TryGetExport(lib, "ctlInit", out var init) || !NativeLibrary.TryGetExport(lib, "ctlEnumerateDevices", out var enumerate) ||
            !NativeLibrary.TryGetExport(lib, "ctlGetSet3DFeature", out var getset) || !NativeLibrary.TryGetExport(lib, "ctlClose", out var close))
            return "unsupported:IGCL incompleto";
        var args = new InitArgs { Size = (uint)sizeof(InitArgs), Version = 0, AppVersion = (1u << 16) | 1u, Flags = 0 };
        IntPtr api;
        if (((delegate* unmanaged[Cdecl]<InitArgs*, IntPtr*, int>)init)(&args, &api) != Success || api == IntPtr.Zero) return "unsupported:IGCL não iniciou";
        try
        {
            uint count = 0;
            if (((delegate* unmanaged[Cdecl]<IntPtr, uint*, IntPtr*, int>)enumerate)(api, &count, null) != Success || count == 0) return "unsupported:nenhuma GPU Intel";
            var devices = new IntPtr[Math.Min(count, 16)];
            fixed (IntPtr* d = devices)
            {
                var n = (uint)devices.Length;
                if (((delegate* unmanaged[Cdecl]<IntPtr, uint*, IntPtr*, int>)enumerate)(api, &n, d) != Success) return "unsupported:IGCL não listou as GPUs";
                count = n;
            }
            string? read = null;
            for (var i = 0; i < count; i++)
                if (OnDevice((delegate* unmanaged[Cdecl]<IntPtr, FeatureGetSet*, int>)getset, devices[i], setting, value) is { } r) read ??= r;
            return read is null ? "unsupported:nenhuma GPU Intel suporta este ajuste" : "ok:" + read;
        }
        finally { ((delegate* unmanaged[Cdecl]<IntPtr, int>)close)(api); }
    }

    private static string? OnDevice(delegate* unmanaged[Cdecl]<IntPtr, FeatureGetSet*, int> getset, IntPtr device, string setting, string? value)
    {
        var (feature, type) = setting switch
        {
            "lowlatency" => (FeatureLowLatency, TypeEnum),
            "endurance" => (FeatureEndurance, TypeCustom),
            "framelimit" => (FeatureFrameLimit, TypeInt32),
            _ => (-1, -1),
        };
        if (feature < 0) return null;
        int* custom = stackalloc int[2]; // ctl_endurance_gaming_t { EGControl, EGMode }
        custom[0] = 0; custom[1] = 0;
        var f = new FeatureGetSet { Size = 56, FeatureType = feature, ValueType = type };
        if (type == TypeCustom) { f.CustomValueSize = 8; f.CustomValue = (IntPtr)custom; }
        if (getset(device, &f) != Success) return null;
        string read = setting switch
        {
            "lowlatency" => f.ValueEnum.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "endurance" => custom[0].ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => f.ValueBool != 0 ? "on" : "off",
        };
        if (value is null) return read;
        var w = new FeatureGetSet { Size = 56, FeatureType = feature, ValueType = type, Set = 1 };
        switch (setting)
        {
            case "lowlatency": w.ValueEnum = uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "endurance":
                custom[0] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); // modo (custom[1]) continua o lido
                w.CustomValueSize = 8; w.CustomValue = (IntPtr)custom; break;
            default: w.ValueBool = value == "on" ? (byte)1 : (byte)0; w.ValueInt = f.ValueInt; break;
        }
        if (getset(device, &w) != Success) throw new InvalidOperationException("IGCL recusou o ajuste.");
        return read;
    }
}
