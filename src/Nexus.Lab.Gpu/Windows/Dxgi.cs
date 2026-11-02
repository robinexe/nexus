using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Lab.Gpu.Windows;

/// <summary>Um adaptador como o DXGI o descreve (DXGI_ADAPTER_DESC1 + orçamento de memória do IDXGIAdapter3).</summary>
internal sealed record DxgiAdapterInfo(string Description, uint VendorId, uint DeviceId, uint SubSysId, uint Revision, ulong DedicatedVideo,
    ulong DedicatedSystem, ulong SharedSystem, long Luid, bool Software, ulong? LocalBudget, ulong? LocalUsage);

/// <summary>
/// DXGI por COM "cru" (chamadas pela vtable, sem pacote NuGet e sem gerar wrappers): CreateDXGIFactory1 →
/// IDXGIFactory1::EnumAdapters1 e, quando existe (Windows 10 1803+), IDXGIFactory6::EnumAdapterByGpuPreference para saber
/// qual é a GPU de alto desempenho e qual é a de economia. Somente leitura: nenhum dispositivo D3D é criado.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Dxgi
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid IidAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    private static readonly Guid IidAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");

    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public WindowsDisplay.Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemoryInfo
    {
        public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, void** factory);

    [LibraryImport("d3d12.dll")]
    private static partial int D3D12CreateDevice(void* adapter, int minimumFeatureLevel, Guid* riid, void** device);

    private static readonly Guid IidD3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
    private const int D3DFeatureLevel11 = 0xb000;
    private const int D3D12FeatureOptions16 = 45; // D3D12_FEATURE_D3D12_OPTIONS16 (d3d12.h, DirectX-Headers)

    [StructLayout(LayoutKind.Sequential)]
    private struct Options16
    {
        public int DynamicDepthBiasSupported;
        public int GpuUploadHeapSupported;
    }

    private static void** Vtbl(void* obj) => *(void***)obj;

    private static int QueryInterface(void* obj, Guid iid, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Vtbl(obj)[0])(obj, &iid, result);

    private static void Release(void* obj)
    {
        if (obj != null) _ = ((delegate* unmanaged[Stdcall]<void*, uint>)Vtbl(obj)[2])(obj);
    }

    /// <summary>Todos os adaptadores na ordem do DXGI, mais os LUIDs preferidos para alto desempenho / economia.</summary>
    public static (IReadOnlyList<DxgiAdapterInfo> Adapters, long? HighPerformance, long? MinimumPower) Enumerate()
    {
        var list = new List<DxgiAdapterInfo>();
        long? high = null, low = null;
        void* factory = null;
        var iid = IidFactory1;
        if (CreateDXGIFactory1(&iid, &factory) < 0 || factory == null) return (list, null, null);
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                void* adapter = null;
                // IDXGIFactory1::EnumAdapters1 = slot 12
                var hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vtbl(factory)[12])(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0 || adapter == null) break;
                try { if (Describe(adapter) is { } info) list.Add(info); }
                finally { Release(adapter); }
            }

            void* f6 = null;
            if (QueryInterface(factory, IidFactory6, &f6) >= 0 && f6 != null)
            {
                try
                {
                    high = FirstByPreference(f6, 2); // DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE
                    low = FirstByPreference(f6, 1);  // DXGI_GPU_PREFERENCE_MINIMUM_POWER
                }
                finally { Release(f6); }
            }
        }
        finally { Release(factory); }
        return (list, high, low);
    }

    /// <summary>
    /// Cria um dispositivo D3D12 temporário no adaptador (pelo LUID) só para perguntar D3D12_OPTIONS16.GPUUploadHeapSupported,
    /// e o solta em seguida. Nada é desenhado nem gravado. Null = sem D3D12, runtime/driver sem a consulta ou adaptador não achado.
    /// </summary>
    public static bool? GpuUploadHeapSupported(long luid)
    {
        void* factory = null;
        var iid = IidFactory1;
        if (CreateDXGIFactory1(&iid, &factory) < 0 || factory == null) return null;
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                void* adapter = null;
                var hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vtbl(factory)[12])(factory, i, &adapter);
                if (hr < 0 || adapter == null) return null;
                try
                {
                    if (Describe(adapter) is not { Software: false } info || info.Luid != luid) continue;
                    void* device = null;
                    var dev = IidD3D12Device;
                    try
                    {
                        if (D3D12CreateDevice(adapter, D3DFeatureLevel11, &dev, &device) < 0 || device == null) return null;
                    }
                    catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
                    try
                    {
                        Options16 o = default;
                        // ID3D12Device::CheckFeatureSupport = slot 13
                        var ok = ((delegate* unmanaged[Stdcall]<void*, int, void*, uint, int>)Vtbl(device)[13])(device, D3D12FeatureOptions16, &o, (uint)sizeof(Options16));
                        return ok < 0 ? null : o.GpuUploadHeapSupported != 0;
                    }
                    finally { Release(device); }
                }
                finally { Release(adapter); }
            }
            return null;
        }
        finally { Release(factory); }
    }

    private static long? FirstByPreference(void* factory6, int preference)
    {
        for (uint i = 0; i < 16; i++)
        {
            void* adapter = null;
            var iid = IidAdapter1;
            // IDXGIFactory6::EnumAdapterByGpuPreference = slot 29
            var hr = ((delegate* unmanaged[Stdcall]<void*, uint, int, Guid*, void**, int>)Vtbl(factory6)[29])(factory6, i, preference, &iid, &adapter);
            if (hr < 0 || adapter == null) return null;
            try
            {
                var info = Describe(adapter);
                if (info is { Software: false }) return info.Luid;
            }
            finally { Release(adapter); }
        }
        return null;
    }

    private static DxgiAdapterInfo? Describe(void* adapter)
    {
        AdapterDesc1 d;
        // IDXGIAdapter1::GetDesc1 = slot 10
        if (((delegate* unmanaged[Stdcall]<void*, AdapterDesc1*, int>)Vtbl(adapter)[10])(adapter, &d) < 0) return null;
        ulong? budget = null, usage = null;
        void* a3 = null;
        if (QueryInterface(adapter, IidAdapter3, &a3) >= 0 && a3 != null)
        {
            try
            {
                VideoMemoryInfo vm;
                // IDXGIAdapter3::QueryVideoMemoryInfo = slot 14 (nó 0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL)
                if (((delegate* unmanaged[Stdcall]<void*, uint, int, VideoMemoryInfo*, int>)Vtbl(a3)[14])(a3, 0, 0, &vm) >= 0)
                {
                    budget = vm.Budget;
                    usage = vm.CurrentUsage;
                }
            }
            finally { Release(a3); }
        }
        return new DxgiAdapterInfo(new string(d.Description).TrimEnd('\0').Trim(), d.VendorId, d.DeviceId, d.SubSysId, d.Revision,
            d.DedicatedVideoMemory, d.DedicatedSystemMemory, d.SharedSystemMemory, d.AdapterLuid.Value, (d.Flags & 0x2) != 0, budget, usage);
    }
}
