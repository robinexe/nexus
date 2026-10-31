using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Lab.Gpu.Windows;

/// <summary>Um adaptador de vídeo presente no gerenciador de dispositivos, com driver e link PCIe.</summary>
internal sealed record PciDisplayDevice(string InstanceId, string? HardwareId, string? Description, string? DriverVersion, DateOnly? DriverDate,
    string? DriverProvider, PcieLinkState Pcie)
{
    public (uint Vendor, uint Device, uint? SubSys)? Ids => GpuIdentity.ParseHardwareId(HardwareId ?? InstanceId);
}

/// <summary>
/// SetupAPI (enumeração da classe Display) + CfgMgr32 (CM_Get_DevNode_PropertyW) para ler, sem driver de kernel,
/// as propriedades que o próprio Windows mantém: versão/data do driver e DEVPKEY_PciDevice_Current/MaxLinkSpeed/Width
/// da placa e da porta (pai) onde ela está ligada.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class PciDevices
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly Guid PciFmtid = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    private static readonly Guid DriverFmtid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly Guid DeviceFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid ParentFmtid = new("4340a6c5-93fa-4706-972c-7b648008a5a7");

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW")]
    private static partial IntPtr SetupDiGetClassDevs(Guid* classGuid, char* enumerator, IntPtr hwnd, uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(IntPtr set, uint index, SpDevInfoData* data);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceId(IntPtr set, SpDevInfoData* data, char* buffer, uint size, uint* required);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CmGetDevNodeProperty(uint devInst, DevPropKey* key, uint* type, byte* buffer, uint* size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CmLocateDevNode(out uint devInst, string deviceId, uint flags);

    private const uint DevPropTypeUInt32 = 0x7, DevPropTypeFileTime = 0x10, DevPropTypeString = 0x12, DevPropTypeStringList = 0x2012;

    public static IReadOnlyList<PciDisplayDevice> DisplayAdapters()
    {
        var list = new List<PciDisplayDevice>();
        var cls = DisplayClass;
        var set = SetupDiGetClassDevs(&cls, null, IntPtr.Zero, 0x2 /* DIGCF_PRESENT */);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        var buf = stackalloc char[512];
        try
        {
            for (uint i = 0; i < 32; i++)
            {
                var data = new SpDevInfoData { CbSize = (uint)sizeof(SpDevInfoData) };
                if (!SetupDiEnumDeviceInfo(set, i, &data)) break;
                buf[0] = '\0';
                uint req;
                var instance = SetupDiGetDeviceInstanceId(set, &data, buf, 512, &req) ? new string(buf) : "";
                list.Add(Read(data.DevInst, instance));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    private static PciDisplayDevice Read(uint dev, string instance)
    {
        var hw = StringList(dev, DeviceFmtid, 3)?.FirstOrDefault(); // DEVPKEY_Device_HardwareIds
        var desc = String(dev, DeviceFmtid, 2);                     // DEVPKEY_Device_DeviceDesc
        var version = String(dev, DriverFmtid, 3);                  // DEVPKEY_Device_DriverVersion
        var provider = String(dev, DriverFmtid, 9);                 // DEVPKEY_Device_DriverProvider
        DateOnly? date = FileTime(dev, DriverFmtid, 2) is { } ft ? DateOnly.FromDateTime(ft) : null; // DEVPKEY_Device_DriverDate

        int? portGen = null, portWidth = null;
        if (String(dev, ParentFmtid, 8) is { Length: > 0 } parent && CmLocateDevNode(out var parentDev, parent, 0) == 0) // DEVPKEY_Device_Parent
        {
            portGen = Pcie.GenFromSpeedCode(UInt(parentDev, PciFmtid, 11));
            portWidth = Pcie.Width(UInt(parentDev, PciFmtid, 12));
        }
        var link = new PcieLinkState(
            Pcie.GenFromSpeedCode(UInt(dev, PciFmtid, 9)),   // CurrentLinkSpeed
            Pcie.GenFromSpeedCode(UInt(dev, PciFmtid, 11)),  // MaxLinkSpeed
            Pcie.Width(UInt(dev, PciFmtid, 10)),             // CurrentLinkWidth
            Pcie.Width(UInt(dev, PciFmtid, 12)),             // MaxLinkWidth
            portGen, portWidth);
        return new PciDisplayDevice(instance, hw, desc, version, date, provider, link);
    }

    private static uint? UInt(uint dev, Guid fmtid, uint pid)
    {
        var key = new DevPropKey { Fmtid = fmtid, Pid = pid };
        uint type, value, size = 4;
        return CmGetDevNodeProperty(dev, &key, &type, (byte*)&value, &size, 0) == 0 && type == DevPropTypeUInt32 ? value : null;
    }

    private static DateTime? FileTime(uint dev, Guid fmtid, uint pid)
    {
        var key = new DevPropKey { Fmtid = fmtid, Pid = pid };
        uint type, size = 8;
        long value;
        if (CmGetDevNodeProperty(dev, &key, &type, (byte*)&value, &size, 0) != 0 || type != DevPropTypeFileTime) return null;
        try { return DateTime.FromFileTimeUtc(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? String(uint dev, Guid fmtid, uint pid)
    {
        var raw = Raw(dev, fmtid, pid, DevPropTypeString);
        return raw?.Split('\0', 2)[0];
    }

    private static string[]? StringList(uint dev, Guid fmtid, uint pid) =>
        Raw(dev, fmtid, pid, DevPropTypeStringList)?.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static string? Raw(uint dev, Guid fmtid, uint pid, uint expectedType)
    {
        var key = new DevPropKey { Fmtid = fmtid, Pid = pid };
        uint type, size = 0;
        _ = CmGetDevNodeProperty(dev, &key, &type, null, &size, 0);
        if (size == 0 || size > 65536) return null;
        var buffer = new byte[size];
        fixed (byte* b = buffer)
        {
            if (CmGetDevNodeProperty(dev, &key, &type, b, &size, 0) != 0 || type != expectedType) return null;
            return new string((char*)b, 0, (int)(size / 2)).TrimEnd('\0');
        }
    }
}
