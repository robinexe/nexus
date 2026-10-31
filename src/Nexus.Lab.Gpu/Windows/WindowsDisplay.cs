using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Lab.Gpu.Windows;

/// <summary>Modo de vídeo pelo user32 (EnumDisplaySettingsExW / ChangeDisplaySettingsExW). Usado pelo handler no aplicador.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayModeBackend : IDisplayModeBackend
{
    public DisplayMode? Current(string device) => WindowsDisplay.CurrentMode(device);

    public IReadOnlyList<DisplayMode> Modes(string device) => WindowsDisplay.ListModes(device);

    public DisplayChangeResult Change(string device, DisplayMode mode, bool test) => WindowsDisplay.Change(device, mode, test);
}

/// <summary>Fora do Windows (testes em Linux): sem monitores.</summary>
public sealed class NoDisplayModeBackend : IDisplayModeBackend
{
    public DisplayMode? Current(string device) => null;
    public IReadOnlyList<DisplayMode> Modes(string device) => [];
    public DisplayChangeResult Change(string device, DisplayMode mode, bool test) => DisplayChangeResult.Failed;
}

/// <summary>Interop de vídeo (user32 + QueryDisplayConfig). Somente leitura, exceto <see cref="Change"/> (só o handler chama).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsDisplay
{
    private const int EnumCurrentSettings = -1;
    private const uint DmBitsPerPel = 0x40000, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
    private const uint DmInterlaced = 0x2;
    private const uint CdsUpdateRegistry = 0x1, CdsTest = 0x2;

    [StructLayout(LayoutKind.Explicit, Size = 220, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(70)] public ushort DriverExtra;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(168)] public uint BitsPerPel;
        [FieldOffset(172)] public uint PelsWidth;
        [FieldOffset(176)] public uint PelsHeight;
        [FieldOffset(180)] public uint DisplayFlags;
        [FieldOffset(184)] public uint DisplayFrequency;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayDevice
    {
        public uint Cb;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettingsEx(string device, int modeNum, DevMode* mode, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ChangeDisplaySettingsEx(string device, DevMode* mode, IntPtr hwnd, uint flags, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayDevices(string? device, uint index, DisplayDevice* info, uint flags);

    private static DevMode NewDevMode() => new() { Size = 220 };

    private static DisplayMode ToMode(in DevMode m) =>
        new((int)m.PelsWidth, (int)m.PelsHeight, (int)m.DisplayFrequency, (int)m.BitsPerPel, (m.DisplayFlags & DmInterlaced) != 0);

    public static DisplayMode? CurrentMode(string device)
    {
        if (!DisplayModes.IsValidDevice(device)) return null;
        var m = NewDevMode();
        return EnumDisplaySettingsEx(device, EnumCurrentSettings, &m, 0) && m.PelsWidth > 0 ? ToMode(m) : null;
    }

    public static IReadOnlyList<DisplayMode> ListModes(string device)
    {
        var list = new List<DisplayMode>();
        if (!DisplayModes.IsValidDevice(device)) return list;
        for (var i = 0; i < 4096; i++)
        {
            var m = NewDevMode();
            if (!EnumDisplaySettingsEx(device, i, &m, 0)) break;
            var mode = ToMode(m);
            if (!list.Contains(mode)) list.Add(mode);
        }
        return list;
    }

    public static DisplayChangeResult Change(string device, DisplayMode mode, bool test)
    {
        if (!DisplayModes.IsValidDevice(device)) return DisplayChangeResult.BadParam;
        var m = NewDevMode();
        if (!EnumDisplaySettingsEx(device, EnumCurrentSettings, &m, 0)) return DisplayChangeResult.Failed;
        m.PelsWidth = (uint)mode.Width;
        m.PelsHeight = (uint)mode.Height;
        m.DisplayFrequency = (uint)mode.Hz;
        m.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency | DmBitsPerPel;
        var r = ChangeDisplaySettingsEx(device, &m, IntPtr.Zero, test ? CdsTest : CdsUpdateRegistry, IntPtr.Zero);
        return Enum.IsDefined(typeof(DisplayChangeResult), r) ? (DisplayChangeResult)r : DisplayChangeResult.Failed;
    }

    /// <summary>Saídas ligadas à área de trabalho: (\\.\DISPLAYn, nome do adaptador, principal).</summary>
    public static IReadOnlyList<(string Device, string Adapter, bool Primary)> Outputs()
    {
        var list = new List<(string, string, bool)>();
        for (uint i = 0; i < 32; i++)
        {
            var d = new DisplayDevice { Cb = (uint)sizeof(DisplayDevice) };
            if (!EnumDisplayDevices(null, i, &d, 0)) break;
            if ((d.StateFlags & 0x1) == 0) continue; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
            list.Add((new string(d.DeviceName), new string(d.DeviceString), (d.StateFlags & 0x4) != 0));
        }
        return list;
    }

    // ------------------------------------------------------------------ QueryDisplayConfig

    /// <summary>LUID com alinhamento de 4 bytes (como no SDK): um <c>long</c> desalinharia as estruturas.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint Low;
        public int High;
        public readonly long Value => ((long)High << 32) | Low;
        public static Luid From(long v) => new() { Low = (uint)(v & 0xFFFFFFFF), High = (int)(v >> 32) };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public Luid SourceAdapter;
        public uint SourceId, SourceModeIdx, SourceStatus;
        public Luid TargetAdapter;
        public uint TargetId, TargetModeIdx, OutputTechnology, Rotation, Scaling, RefreshNum, RefreshDen, ScanLine;
        public int TargetAvailable;
        public uint TargetStatus;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Type;
        public uint Size;
        public Luid Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceName
    {
        public Header Header;
        public fixed char GdiName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetName
    {
        public Header Header;
        public uint Flags, OutputTechnology;
        public ushort EdidManufacture, EdidProduct;
        public uint ConnectorInstance;
        public fixed char FriendlyName[64];
        public fixed char DevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColor
    {
        public Header Header;
        public uint Bits;
        public uint ColorEncoding, BitsPerColorChannel;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint numPaths, PathInfo* paths, ref uint numModes, byte* modes, IntPtr topology);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(Header* packet);

    /// <summary>Uma saída ativa como o QueryDisplayConfig a vê.</summary>
    internal sealed record ActivePath(string Device, long AdapterLuid, uint OutputTechnology, string FriendlyName, double RefreshHz, bool? HdrSupported, bool? HdrEnabled, int? BitsPerColor);

    public static IReadOnlyList<ActivePath> ActivePaths()
    {
        const uint QdcOnlyActivePaths = 0x2;
        var result = new List<ActivePath>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var np, out var nm) != 0) return result;
            var paths = new PathInfo[np];
            var modes = new byte[Math.Max(1, nm) * 64];
            int rc;
            fixed (PathInfo* p = paths)
            fixed (byte* m = modes)
                rc = QueryDisplayConfig(QdcOnlyActivePaths, ref np, p, ref nm, m, IntPtr.Zero);
            if (rc == 122) continue; // ERROR_INSUFFICIENT_BUFFER: a topologia mudou entre as chamadas
            if (rc != 0) return result;
            for (var i = 0; i < np; i++)
            {
                var path = paths[i];
                var src = new SourceName { Header = new Header { Type = 1, Size = (uint)sizeof(SourceName), Adapter = path.SourceAdapter, Id = path.SourceId } };
                if (DisplayConfigGetDeviceInfo(&src.Header) != 0) continue;
                var tgt = new TargetName { Header = new Header { Type = 2, Size = (uint)sizeof(TargetName), Adapter = path.TargetAdapter, Id = path.TargetId } };
                var friendly = DisplayConfigGetDeviceInfo(&tgt.Header) == 0 ? new string(tgt.FriendlyName) : "";
                var color = new AdvancedColor { Header = new Header { Type = 9, Size = (uint)sizeof(AdvancedColor), Adapter = path.TargetAdapter, Id = path.TargetId } };
                bool? hdrSup = null, hdrOn = null;
                int? bpc = null;
                if (DisplayConfigGetDeviceInfo(&color.Header) == 0)
                {
                    hdrSup = (color.Bits & 0x1) != 0;
                    hdrOn = (color.Bits & 0x2) != 0;
                    bpc = color.BitsPerColorChannel is > 0 and <= 16 ? (int)color.BitsPerColorChannel : null;
                }
                var hz = path.RefreshDen == 0 ? 0 : (double)path.RefreshNum / path.RefreshDen;
                result.Add(new ActivePath(new string(src.GdiName), path.SourceAdapter.Value, path.OutputTechnology, friendly, hz, hdrSup, hdrOn, bpc));
            }
            return result;
        }
        return result;
    }

    /// <summary>Tela interna (painel do notebook): LVDS, eDP, UDI embutido ou "interno".</summary>
    public static bool IsInternal(uint tech) => tech is 6 or 11 or 13 or 0x80000000;

    public static string ConnectionLabel(uint tech) => tech switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS (interna)",
        10 => "DisplayPort",
        11 => "eDP (interna)",
        13 => "interna",
        12 => "UDI",
        15 => "Miracast",
        16 or 17 => "indireta (adaptador USB/virtual)",
        18 => "DisplayPort por USB-C",
        0x80000000 => "interna",
        _ => "outra",
    };
}
