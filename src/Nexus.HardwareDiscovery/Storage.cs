namespace Nexus.HardwareDiscovery;

/// <summary>Tipo de mídia de um disco físico.</summary>
public enum DiskMedia
{
    /// <summary>Não deu para saber (o disco/driver não respondeu às consultas). Nunca é chutado.</summary>
    Unknown,
    /// <summary>Disco rígido (tem "penalidade de busca": cabeça mecânica).</summary>
    Hdd,
    /// <summary>SSD (sem penalidade de busca, ou barramento NVMe).</summary>
    Ssd,
    /// <summary>Memória de classe de armazenamento (ex.: Optane persistente; barramento SCM).</summary>
    Scm,
}

/// <summary>Um disco físico (\\.\PhysicalDriveN) como o próprio disco se descreve.</summary>
/// <param name="Number">Número do disco no Windows (o mesmo do Gerenciamento de Disco).</param>
/// <param name="MediaSource">Como o tipo foi determinado (para a evidência).</param>
/// <param name="Volumes">Letras das unidades fixas que ficam neste disco (ex.: "C:\").</param>
public sealed record PhysicalDiskFacts(int Number, string? Model, DiskMedia Media, string? Bus, ulong? SizeBytes, string MediaSource, IReadOnlyList<string> Volumes)
{
    /// <summary>"SSD NVMe", "SSD (SATA)", "HD (SATA)", "desconhecido".</summary>
    public string MediaText => StorageClassifier.Describe(Media, Bus);
}

/// <summary>Respostas cruas do disco às consultas IOCTL (null = a consulta falhou ou não se aplica).</summary>
/// <param name="IncursSeekPenalty">StorageDeviceSeekPenaltyProperty: true = HDD; false = SSD.</param>
/// <param name="BusType">STORAGE_BUS_TYPE (StorageDeviceProperty): 17 = NVMe, 11 = SATA, 7 = USB, 18 = SCM…</param>
public sealed record RawDiskInfo(bool? IncursSeekPenalty, int? BusType, string? Vendor, string? Product, ulong? SizeBytes);

/// <summary>Consultas de armazenamento, trocáveis nos testes. Nenhuma escreve nem exige administrador.</summary>
public interface IStorageQuery
{
    /// <summary>Discos físicos que respondem (números).</summary>
    IReadOnlyList<int> DiskNumbers();

    /// <summary>Propriedades de um disco físico (null = não abriu).</summary>
    RawDiskInfo? Query(int diskNumber);

    /// <summary>Discos em que um volume ("C:\") está (vazio = não deu para saber; mais de um = volume estendido/espelhado).</summary>
    IReadOnlyList<int> DisksOfVolume(string root);
}

/// <summary>Regras puras: tipo de mídia a partir das respostas cruas, nomes de barramento e textos.</summary>
public static class StorageClassifier
{
    public const int BusNvme = 17;
    public const int BusScm = 18;

    /// <summary>
    /// Penalidade de busca vence (é o mesmo critério do "Otimizar unidades" do Windows); sem ela, barramento NVMe = SSD e
    /// SCM = SCM. Sem nenhuma das duas: desconhecido (nunca "provavelmente SSD").
    /// </summary>
    public static (DiskMedia Media, string Source) Classify(RawDiskInfo raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.BusType == BusScm) return (DiskMedia.Scm, "barramento SCM informado pelo disco (IOCTL)");
        return raw.IncursSeekPenalty switch
        {
            true => (DiskMedia.Hdd, "penalidade de busca informada pelo disco (IOCTL_STORAGE_QUERY_PROPERTY)"),
            false => (DiskMedia.Ssd, "sem penalidade de busca, informado pelo disco (IOCTL_STORAGE_QUERY_PROPERTY)"),
            null when raw.BusType == BusNvme => (DiskMedia.Ssd, "barramento NVMe informado pelo disco (IOCTL)"),
            _ => (DiskMedia.Unknown, "o disco não informou penalidade de busca nem barramento NVMe"),
        };
    }

    /// <summary>Nome do barramento a partir de STORAGE_BUS_TYPE (null = desconhecido).</summary>
    public static string? BusName(int? busType) => busType switch
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
        _ => null,
    };

    public static string Describe(DiskMedia media, string? bus) => media switch
    {
        DiskMedia.Ssd when bus == "NVMe" => "SSD NVMe",
        DiskMedia.Ssd => bus is null ? "SSD" : $"SSD ({bus})",
        DiskMedia.Hdd => bus is null ? "HD" : $"HD ({bus})",
        DiskMedia.Scm => "SCM (memória persistente)",
        _ => "desconhecido",
    };

    /// <summary>"Samsung SSD 980" a partir de vendor + produto do STORAGE_DEVICE_DESCRIPTOR (sem repetir, sem "NVMe" de enfeite).</summary>
    public static string? Model(string? vendor, string? product)
    {
        var v = vendor?.Trim();
        var p = product?.Trim();
        if (string.Equals(v, "NVMe", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "ATA", StringComparison.OrdinalIgnoreCase)) v = null;
        if (!string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(p) && p.StartsWith(v, StringComparison.OrdinalIgnoreCase)) v = null;
        var text = string.Join(" ", new[] { v, p }.Where(s => !string.IsNullOrEmpty(s)));
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Lê todos os discos e liga cada unidade fixa ao seu disco. O tipo do disco do sistema é o do disco do volume do
    /// sistema; volume em mais de um disco com tipos diferentes = desconhecido.
    /// </summary>
    public static (IReadOnlyList<PhysicalDiskFacts> Disks, IReadOnlyDictionary<string, DiskMedia> VolumeMedia) Read(IStorageQuery query, IEnumerable<string> fixedRoots)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(fixedRoots);
        var volumesByDisk = new Dictionary<int, List<string>>();
        var disksByVolume = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in fixedRoots)
        {
            IReadOnlyList<int> disks;
            try { disks = query.DisksOfVolume(root); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { disks = []; }
            disksByVolume[root] = disks;
            foreach (var d in disks)
            {
                if (!volumesByDisk.TryGetValue(d, out var list)) volumesByDisk[d] = list = [];
                if (!list.Contains(root, StringComparer.OrdinalIgnoreCase)) list.Add(root);
            }
        }

        var numbers = new SortedSet<int>(query.DiskNumbers());
        foreach (var d in volumesByDisk.Keys) numbers.Add(d);
        var result = new List<PhysicalDiskFacts>();
        foreach (var n in numbers)
        {
            RawDiskInfo? raw;
            try { raw = query.Query(n); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { raw = null; }
            var vols = volumesByDisk.TryGetValue(n, out var v) ? (IReadOnlyList<string>)v : [];
            if (raw is null)
            {
                result.Add(new PhysicalDiskFacts(n, null, DiskMedia.Unknown, null, null, "o disco não respondeu à consulta", vols));
                continue;
            }
            var (media, source) = Classify(raw);
            result.Add(new PhysicalDiskFacts(n, Model(raw.Vendor, raw.Product), media, BusName(raw.BusType), raw.SizeBytes, source, vols));
        }

        var byNumber = result.ToDictionary(d => d.Number);
        var volumeMedia = new Dictionary<string, DiskMedia>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, disks) in disksByVolume)
        {
            var kinds = disks.Select(d => byNumber.TryGetValue(d, out var f) ? f.Media : DiskMedia.Unknown).Distinct().ToList();
            volumeMedia[root] = kinds.Count == 1 ? kinds[0] : DiskMedia.Unknown;
        }
        return (result, volumeMedia);
    }
}

/// <summary>Conselhos que dependem do tipo do disco do sistema (puro, testado).</summary>
public static class StorageAdvice
{
    /// <summary>Id do preset de serviços pensado só para PC com SSD (catalog/services.v1.json).</summary>
    public const string SsdServicesPreset = "jogos-ssd";

    /// <summary>
    /// Aviso para presets que só valem em SSD: em HD o SysMain (pré-carregamento) costuma ajudar, então o preset não é
    /// para este PC. Null = nada a avisar (SSD confirmado ou preset que não depende do disco).
    /// </summary>
    public static string? PresetWarning(string presetId, DiskMedia systemDisk) =>
        !string.Equals(presetId, SsdServicesPreset, StringComparison.Ordinal) ? null : systemDisk switch
        {
            DiskMedia.Hdd => "ATENÇÃO: o Windows deste PC está num HD (disco rígido). Este preset é para SSD: em HD o SysMain pré-carrega " +
                             "os programas que você mais usa e costuma deixá-los abrir mais rápido. Não recomendado aqui.",
            DiskMedia.Ssd or DiskMedia.Scm => null,
            _ => "Não deu para confirmar se o Windows está num SSD. Confira antes: em HD este preset pode deixar os programas abrirem mais devagar.",
        };
}
