using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Nexus.DiskTools.Volumes;

/// <summary>Tipo de mídia do disco por trás de um volume.</summary>
public enum MediaKind { Unknown, Ssd, Hdd }

/// <summary>Mídia de um volume: SSD/HDD (pela "penalidade de busca" informada pelo disco) e barramento (NVMe, SATA, USB...).</summary>
public sealed record StorageMediaInfo(MediaKind Kind, string Bus, string? Model, string Source)
{
    public static readonly StorageMediaInfo Unknown = new(MediaKind.Unknown, "desconhecido", null, "indisponível");

    public bool IsSolidState => Kind == MediaKind.Ssd;

    public string Describe() => Kind switch
    {
        MediaKind.Ssd when Bus == "NVMe" => "SSD NVMe",
        MediaKind.Ssd => Bus is "desconhecido" ? "SSD" : $"SSD ({Bus})",
        MediaKind.Hdd => Bus is "desconhecido" ? "HD (disco giratório)" : $"HD ({Bus})",
        _ => Bus is "desconhecido" ? "tipo desconhecido" : Bus,
    };
}

/// <summary>Uma unidade com espaço livre/total e tipo de mídia (somente leitura).</summary>
public sealed record VolumeInfo(string RootPath, string Label, string FileSystem, DriveType DriveType, long TotalBytes, long FreeBytes, StorageMediaInfo Media)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
    public string Letter => RootPath.TrimEnd('\\', '/');
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Letter : $"{Label} ({Letter})";
}

/// <summary>Lista as unidades prontas (fixas e removíveis). Só lê.</summary>
public static class VolumeCatalog
{
    public static IReadOnlyList<VolumeInfo> List()
    {
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) continue;
                var media = OperatingSystem.IsWindows() && d.DriveType is DriveType.Fixed or DriveType.Removable && d.Name.Length >= 2 && d.Name[1] == ':'
                    ? StorageMedia.Query(d.Name[0])
                    : StorageMediaInfo.Unknown;
                list.Add(new VolumeInfo(d.Name, SafeLabel(d), d.DriveFormat, d.DriveType, d.TotalSize, d.AvailableFreeSpace, media));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Volume que contém um caminho (ou null).</summary>
    public static VolumeInfo? ForPath(IEnumerable<VolumeInfo> volumes, string path)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        var root = Path.GetPathRoot(path);
        return root is null ? null : volumes.FirstOrDefault(v => string.Equals(v.RootPath.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Consulta o disco por <c>IOCTL_STORAGE_QUERY_PROPERTY</c> no volume (<c>\\.\C:</c>), aberto sem direitos de leitura ou
/// escrita (não exige administrador): <c>StorageDeviceSeekPenaltyProperty</c> (sem penalidade de busca = SSD) e
/// <c>StorageDeviceProperty</c> (barramento e modelo). Falhou (volume em vários discos, driver que não responde) = desconhecido.
/// </summary>
public static partial class StorageMedia
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    [SupportedOSPlatform("windows")]
    public static StorageMediaInfo Query(char driveLetter)
    {
        if (!char.IsAsciiLetter(driveLetter)) return StorageMediaInfo.Unknown;
        using var h = CreateFile($@"\\.\{char.ToUpperInvariant(driveLetter)}:", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) return StorageMediaInfo.Unknown;

        var (bus, model) = QueryDevice(h);
        bool? seekPenalty = QuerySeekPenalty(h);
        var kind = seekPenalty switch
        {
            false => MediaKind.Ssd,
            true => MediaKind.Hdd,
            null when bus == "NVMe" => MediaKind.Ssd,
            _ => MediaKind.Unknown,
        };
        return new StorageMediaInfo(kind, bus, model, seekPenalty is null ? "barramento do disco (IOCTL)" : "penalidade de busca informada pelo disco (IOCTL)");
    }

    /// <summary>Nome do barramento a partir de STORAGE_BUS_TYPE.</summary>
    public static string BusName(int busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "FireWire",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "Disco virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "desconhecido",
    };

    [SupportedOSPlatform("windows")]
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

    [SupportedOSPlatform("windows")]
    private static unsafe (string Bus, string? Model) QueryDevice(SafeFileHandle h)
    {
        var query = stackalloc int[3];
        query[0] = StorageDeviceProperty;
        query[1] = 0;
        query[2] = 0;
        const int size = 1024;
        var buffer = new byte[size];
        fixed (byte* output = buffer)
        {
            if (!DeviceIoControl(h, IoctlStorageQueryProperty, query, 12, output, size, out var returned, IntPtr.Zero) || returned < 32)
                return ("desconhecido", null);
        }
        var span = buffer.AsSpan();
        var bus = BusName(BinaryPrimitives.ReadInt32LittleEndian(span[28..]));
        var productOffset = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
        string? model = null;
        if (productOffset > 0 && productOffset < size)
        {
            var end = span[productOffset..].IndexOf((byte)0);
            if (end > 0) model = System.Text.Encoding.ASCII.GetString(span.Slice(productOffset, end)).Trim();
        }
        return (bus, string.IsNullOrWhiteSpace(model) ? null : model);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize,
        out uint returned, IntPtr overlapped);
}
