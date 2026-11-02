using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Nexus.HardwareDiscovery;

/// <summary>
/// SSD/HDD pelo próprio disco, sem administrador e sem WMI: <c>\\.\PhysicalDriveN</c> e <c>\\.\C:</c> são abertos SEM
/// direitos de leitura ou escrita (acesso 0, só consulta de propriedades) e consultados com IOCTLs FILE_ANY_ACCESS:
/// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> (StorageDeviceSeekPenaltyProperty — o mesmo critério do "Otimizar unidades" — e
/// StorageDeviceProperty: barramento e modelo), <c>IOCTL_DISK_GET_DRIVE_GEOMETRY_EX</c> (tamanho) e
/// <c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c> (em que disco está cada unidade). Nada é lido do conteúdo do disco.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsStorageQuery : IStorageQuery
{
    public static WindowsStorageQuery Instance { get; } = new();

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const int MaxDisks = 32;

    public IReadOnlyList<int> DiskNumbers()
    {
        var found = new List<int>();
        for (var n = 0; n < MaxDisks; n++)
        {
            using var h = OpenDisk(n);
            if (!h.IsInvalid) found.Add(n);
        }
        return found;
    }

    public RawDiskInfo? Query(int diskNumber)
    {
        using var h = OpenDisk(diskNumber);
        if (h.IsInvalid) return null;
        var (bus, vendor, product) = QueryDevice(h);
        return new RawDiskInfo(QuerySeekPenalty(h), bus, vendor, product, QuerySize(h));
    }

    public unsafe IReadOnlyList<int> DisksOfVolume(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Length < 2 || !char.IsAsciiLetter(root[0]) || root[1] != ':') return [];
        using var h = CreateFile($@"\\.\{char.ToUpperInvariant(root[0])}:", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) return [];
        const int extents = 16;
        const int size = 8 + 24 * extents; // VOLUME_DISK_EXTENTS: DWORD count (+4 de alinhamento), DISK_EXTENT[24 bytes]
        var buffer = stackalloc byte[size];
        if (!DeviceIoControl(h, IoctlVolumeGetVolumeDiskExtents, null, 0, buffer, size, out var returned, IntPtr.Zero) || returned < 8) return [];
        var span = new ReadOnlySpan<byte>(buffer, (int)returned);
        var count = Math.Min(BinaryPrimitives.ReadInt32LittleEndian(span), extents);
        var disks = new List<int>();
        for (var i = 0; i < count && 8 + 24 * (i + 1) <= span.Length; i++)
        {
            var disk = BinaryPrimitives.ReadInt32LittleEndian(span[(8 + 24 * i)..]);
            if (!disks.Contains(disk)) disks.Add(disk);
        }
        return disks;
    }

    private static SafeFileHandle OpenDisk(int n) =>
        CreateFile($@"\\.\PhysicalDrive{n}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    private static unsafe bool? QuerySeekPenalty(SafeFileHandle h)
    {
        var query = stackalloc int[3];
        query[0] = StorageDeviceSeekPenaltyProperty;
        query[1] = 0; // PropertyStandardQuery
        query[2] = 0;
        var output = stackalloc byte[12]; // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, BOOLEAN IncursSeekPenalty
        if (!DeviceIoControl(h, IoctlStorageQueryProperty, query, 12, output, 12, out var returned, IntPtr.Zero) || returned < 9) return null;
        return output[8] != 0;
    }

    private static unsafe (int? Bus, string? Vendor, string? Product) QueryDevice(SafeFileHandle h)
    {
        var query = stackalloc int[3];
        query[0] = StorageDeviceProperty;
        query[1] = 0;
        query[2] = 0;
        const int size = 1024;
        var buffer = new byte[size];
        uint returned;
        fixed (byte* output = buffer)
        {
            if (!DeviceIoControl(h, IoctlStorageQueryProperty, query, 12, output, size, out returned, IntPtr.Zero) || returned < 32)
                return (null, null, null);
        }
        // STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset @12, ProductIdOffset @16, BusType @28.
        var span = buffer.AsSpan(0, (int)Math.Min(returned, (uint)size));
        var bus = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
        return (bus, Ascii(span, BinaryPrimitives.ReadInt32LittleEndian(span[12..])), Ascii(span, BinaryPrimitives.ReadInt32LittleEndian(span[16..])));
    }

    private static string? Ascii(ReadOnlySpan<byte> span, int offset)
    {
        if (offset <= 0 || offset >= span.Length) return null;
        var end = span[offset..].IndexOf((byte)0);
        if (end < 0) end = span.Length - offset;
        var text = System.Text.Encoding.ASCII.GetString(span.Slice(offset, end)).Trim();
        return text.Length == 0 ? null : text;
    }

    private static unsafe ulong? QuerySize(SafeFileHandle h)
    {
        var output = stackalloc byte[256]; // DISK_GEOMETRY_EX: DISK_GEOMETRY (24 bytes) + LARGE_INTEGER DiskSize @24 + dados
        if (!DeviceIoControl(h, IoctlDiskGetDriveGeometryEx, null, 0, output, 256, out var returned, IntPtr.Zero) || returned < 32) return null;
        var size = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(output + 24, 8));
        return size > 0 ? (ulong)size : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize,
        out uint returned, IntPtr overlapped);
}
