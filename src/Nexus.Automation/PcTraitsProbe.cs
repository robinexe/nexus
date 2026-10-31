using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Nexus.HardwareDiscovery;

namespace Nexus.Automation;

/// <summary>
/// Leitura SOMENTE LEITURA das características extras do PC (<see cref="PcTraits"/>). Cada parte falha isolada: o que não der
/// para ler fica null. Fora do Windows, tudo null.
/// </summary>
public static partial class PcTraitsProbe
{
    public static PcTraits Read(string? systemDrive = null)
    {
        if (!OperatingSystem.IsWindows()) return PcTraits.Unknown;
        return ReadWindows(systemDrive ?? Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
    }

    [SupportedOSPlatform("windows")]
    private static PcTraits ReadWindows(string systemDrive)
    {
        int? chassis = null;
        bool? battery = null, ac = null, ssd = null;
        int? percent = null;
        string? bus = null;
        try { chassis = ReadChassisType(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try
        {
            if (GetSystemPowerStatus(out var s) != 0)
            {
                battery = s.BatteryFlag switch { 255 => null, _ => (s.BatteryFlag & 128) == 0 };
                ac = s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
                percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { (ssd, bus) = ReadSsd(systemDrive); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new PcTraits(chassis, battery, ac, percent, ssd, bus);
    }

    /// <summary>SMBIOS tipo 3 (System Enclosure), byte 05h: tipo do gabinete (bit 7 = trava, ignorado).</summary>
    public static int? ChassisTypeFrom(SmbiosParser.Table table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var enclosure = table.Structures.FirstOrDefault(s => s.Type == 3);
        return enclosure?.Byte(0x05) is { } b ? b & 0x7F : null;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe int? ReadChassisType()
    {
        const uint rsmb = 0x52534D42; // 'RSMB'
        var size = GetSystemFirmwareTable(rsmb, 0, null, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            var got = GetSystemFirmwareTable(rsmb, 0, p, size);
            if (got == 0 || got > size) return null;
        }
        return ChassisTypeFrom(SmbiosParser.Parse(buffer));
    }

    /// <summary>
    /// SSD do disco do sistema: IOCTL_STORAGE_QUERY_PROPERTY com StorageDeviceSeekPenaltyProperty (sem penalidade de busca = SSD)
    /// e, como apoio, o barramento (NVMe = SSD). O volume é aberto SEM direitos de acesso (só consulta de propriedades).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe (bool? Ssd, string? Bus) ReadSsd(string systemDrive)
    {
        var letter = systemDrive.TrimEnd('\\', ':');
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0])) return (null, null);
        using var handle = CreateFileW($@"\\.\{letter}:", 0, 3 /* FILE_SHARE_READ|WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (handle.IsInvalid) return (null, null);

        bool? ssd = null;
        string? bus = null;
        var query = new StoragePropertyQuery { PropertyId = 7 /* StorageDeviceSeekPenaltyProperty */, QueryType = 0 };
        SeekPenaltyDescriptor seek;
        if (DeviceIoControl(handle, IoctlStorageQueryProperty, &query, (uint)sizeof(StoragePropertyQuery), &seek, (uint)sizeof(SeekPenaltyDescriptor), out var got, IntPtr.Zero) != 0
            && got >= 9)
            ssd = seek.IncursSeekPenalty == 0;

        query.PropertyId = 0; // StorageDeviceProperty
        var desc = stackalloc byte[1024];
        if (DeviceIoControl(handle, IoctlStorageQueryProperty, &query, (uint)sizeof(StoragePropertyQuery), desc, 1024, out got, IntPtr.Zero) != 0 && got >= 32)
        {
            var busType = desc[28]; // STORAGE_DEVICE_DESCRIPTOR.BusType
            bus = BusName(busType);
            if (busType == 17) ssd ??= true; // NVMe
        }
        return (ssd, bus);
    }

    public static string BusName(int busType) => busType switch
    {
        1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual",
        15 => "FileBackedVirtual", 16 => "Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => $"tipo {busType}",
    };

    private const uint IoctlStorageQueryProperty = 0x002D1400;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetSystemFirmwareTable(uint provider, uint tableId, byte* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize, out uint returned, IntPtr overlapped);
}
