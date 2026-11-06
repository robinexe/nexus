using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.HardwareDiscovery;

namespace Nexus.Automation;

/// <summary>
/// Leituras extras (somente leitura) que a descoberta ainda não traz: formato do gabinete (SMBIOS tipo 3), bateria/tomada
/// e se o disco do sistema é SSD. Null = não determinado (nunca presumido).
/// </summary>
public sealed record PcTraits(
    int? ChassisType = null,
    bool? HasSystemBattery = null,
    bool? OnAcPower = null,
    int? BatteryPercent = null,
    bool? SystemDriveIsSsd = null,
    string? SystemDriveBus = null)
{
    public static readonly PcTraits Unknown = new();

    /// <summary>Tipos de gabinete SMBIOS portáteis (DMTF DSP0134, 7.4.1): portátil, laptop, notebook, sub-notebook, tablet, conversível, destacável.</summary>
    public static bool? IsPortableChassis(int? chassis) => chassis switch
    {
        null or 1 or 2 => null, // "Other"/"Unknown": não dá para dizer
        8 or 9 or 10 or 11 or 14 or 30 or 31 or 32 => true,
        _ => false,
    };
}

public enum FormFactor { Unknown, Desktop, Laptop, VirtualMachine }

/// <summary>Perfil do PC: o que ele é e as etiquetas usadas pelas regras de recomendação.</summary>
/// <param name="Headline">Ex.: "Notebook gamer · RTX 4060 · 16 GB · SSD".</param>
/// <param name="Kind">Ex.: "Notebook gamer".</param>
/// <param name="Tags">Etiquetas (laptop, on-battery, gpu-discrete, hdd, low-ram, diag:win.gamemode:attention...).</param>
/// <param name="Notes">Como cada parte foi decidida (fonte ou "não determinado").</param>
public sealed record PcProfile(
    FormFactor Form,
    string Kind,
    string Headline,
    string? GpuShortName,
    int? MemoryGb,
    bool? Ssd,
    IReadOnlySet<string> Tags,
    IReadOnlyList<string> Notes)
{
    public bool Has(string tag) => Tags.Contains(tag);
}

/// <summary>
/// Classificação PURA do PC a partir da descoberta (<see cref="SystemFacts"/>), das leituras extras (<see cref="PcTraits"/>) e
/// do diagnóstico existente (<see cref="DiagnosticRules"/>). Nada é inventado: sem dado, a etiqueta não aparece.
/// </summary>
public static partial class PcProfiler
{
    public const ulong Gb = 1024UL * 1024 * 1024;

    // Etiquetas (contrato com as regras JSON).
    public const string Laptop = "laptop", Desktop = "desktop", Vm = "vm", FormUnknown = "form-unknown";
    public const string OnBattery = "on-battery", OnAc = "on-ac";
    public const string GpuDiscrete = "gpu-discrete", GpuIntegratedOnly = "gpu-integrated-only";
    public const string Gamer = "gamer", LowEnd = "low-end", LowRam = "low-ram", Ram16 = "ram-16plus";
    public const string Ssd = "ssd", Hdd = "hdd", DiskUnknown = "disk-unknown", LowDisk = "low-disk", FewCores = "few-cores";
    public const string CpuAmd = "cpu-amd", CpuIntel = "cpu-intel", Win11 = "win11", Win10 = "win10", Elevated = "elevated";

    public static PcProfile Classify(SystemFacts facts, PcTraits? traits = null, IReadOnlyList<DiagnosticFinding>? findings = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        traits ??= PcTraits.Unknown;
        findings ??= DiagnosticRules.Evaluate(facts);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();

        // Formato
        var portable = PcTraits.IsPortableChassis(traits.ChassisType);
        FormFactor form;
        if (facts.IsVirtualMachine == true) { form = FormFactor.VirtualMachine; tags.Add(Vm); notes.Add($"Máquina virtual (SMBIOS: {facts.SystemManufacturer} / {facts.SystemModel})."); }
        else if (portable == true) { form = FormFactor.Laptop; tags.Add(Laptop); notes.Add($"Notebook: gabinete SMBIOS tipo {traits.ChassisType}."); }
        else if (portable is null && traits.HasSystemBattery == true) { form = FormFactor.Laptop; tags.Add(Laptop); notes.Add("Notebook (inferido): há bateria do sistema e o gabinete não foi identificado."); }
        else if (portable == false) { form = FormFactor.Desktop; tags.Add(Desktop); notes.Add($"Desktop: gabinete SMBIOS tipo {traits.ChassisType}."); }
        else { form = FormFactor.Unknown; tags.Add(FormUnknown); notes.Add("Formato do PC não determinado (gabinete SMBIOS indisponível)."); }

        // Energia
        if (traits.OnAcPower == false && traits.HasSystemBattery == true) { tags.Add(OnBattery); notes.Add($"Na bateria{(traits.BatteryPercent is { } p ? $" ({p}%)" : "")}."); }
        else if (traits.OnAcPower == true) tags.Add(OnAc);

        // GPU
        var gpus = facts.Gpus.Where(g => !IsSoftwareAdapter(g.Name)).ToList();
        var discrete = gpus.Where(IsDiscrete).ToList();
        string? gpuShort = null;
        if (discrete.Count > 0) { tags.Add(GpuDiscrete); gpuShort = ShortGpuName(discrete[0].Name); }
        else if (gpus.Count > 0) { tags.Add(GpuIntegratedOnly); gpuShort = ShortGpuName(gpus[0].Name); }
        if (discrete.Count > 0 && form != FormFactor.VirtualMachine) tags.Add(Gamer);

        // Memória (soma dos módulos SMBIOS quando houver; senão o total visível arredondado)
        int? memGb = null;
        var modules = facts.MemoryModules.Select(m => m.SizeBytes).Where(s => s is not null).Sum(s => (decimal)s!.Value);
        if (modules > 0) memGb = (int)Math.Round(modules / Gb);
        else if (facts.TotalMemoryBytes is { } total) memGb = (int)Math.Round((double)total / Gb);
        if (memGb is { } gb)
        {
            if (gb <= 8) tags.Add(LowRam); // mesmo limite do HardwareProfile (8 GB ou menos)
            if (gb >= 16) tags.Add(Ram16);
        }

        // Disco do sistema
        bool? ssd = traits.SystemDriveIsSsd;
        tags.Add(ssd switch { true => Ssd, false => Hdd, null => DiskUnknown });
        if (ssd is null) notes.Add("Tipo do disco do sistema (SSD/HDD) não determinado.");
        var sys = facts.Drives.FirstOrDefault(d => string.Equals(d.Name, facts.SystemDrive, StringComparison.OrdinalIgnoreCase));
        if (sys is { TotalBytes: > 0, FreeBytes: { } free } && (free * 10 < sys.TotalBytes || free < 15 * Gb)) tags.Add(LowDisk);

        // CPU
        if (facts.LogicalProcessors is <= 4) tags.Add(FewCores);
        if (string.Equals(facts.CpuVendor, "AMD", StringComparison.OrdinalIgnoreCase)) tags.Add(CpuAmd);
        else if (string.Equals(facts.CpuVendor, "Intel", StringComparison.OrdinalIgnoreCase)) tags.Add(CpuIntel);

        if (tags.Contains(LowRam) || tags.Contains(Hdd) || tags.Contains(FewCores)) tags.Add(LowEnd);

        // Windows
        if (facts.WindowsBuild is { } build) tags.Add(build >= 22000 ? Win11 : Win10);
        if (facts.IsElevated == true) tags.Add(Elevated);

        // Diagnóstico existente vira etiqueta: diag:<id>:<severidade>
        foreach (var f in findings)
            tags.Add($"diag:{f.Id}:{f.Severity.ToString().ToLowerInvariant()}");

        var kind = KindText(form, tags);
        var parts = new List<string> { kind };
        if (gpuShort is not null) parts.Add(gpuShort);
        if (memGb is { } m) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{m} GB"));
        if (ssd is { } isSsd) parts.Add(isSsd ? "SSD" : "HDD");
        return new PcProfile(form, kind, string.Join(" · ", parts), gpuShort, memGb, ssd, tags, notes);
    }

    private static string KindText(FormFactor form, HashSet<string> tags)
    {
        var baseText = form switch
        {
            FormFactor.VirtualMachine => "Máquina virtual",
            FormFactor.Laptop => tags.Contains(Gamer) ? "Notebook gamer" : "Notebook",
            FormFactor.Desktop => tags.Contains(Gamer) ? "Desktop gamer" : "Desktop",
            _ => tags.Contains(Gamer) ? "PC gamer" : "PC",
        };
        if (tags.Contains(LowEnd) && !tags.Contains(Gamer) && form != FormFactor.VirtualMachine) baseText += " modesto";
        if (tags.Contains(OnBattery)) baseText += " na bateria";
        return baseText;
    }

    /// <summary>Adaptadores que não são GPU de verdade (Microsoft Basic Display, RDP, Hyper-V).</summary>
    public static bool IsSoftwareAdapter(string name) =>
        name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(RX|R9|R7|HD)\s*\d{3,4}|Radeon\s+Pro|Radeon\s+VII|FirePro", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmdDiscrete();

    [GeneratedRegex(@"Arc(\(TM\))?\s+[AB]\d{3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelDiscrete();

    /// <summary>
    /// GPU dedicada pelo nome (o fornecedor sozinho não basta: AMD e Intel têm integradas). NVIDIA GeForce/RTX/Quadro = dedicada;
    /// AMD "RX 6600", "Radeon Pro" = dedicada, "Radeon(TM) Graphics"/"Vega 8" = integrada; Intel "Arc A770" = dedicada.
    /// </summary>
    public static bool IsDiscrete(GpuInfo g)
    {
        ArgumentNullException.ThrowIfNull(g);
        var name = g.Name ?? "";
        if (IsSoftwareAdapter(name)) return false;
        var vendor = g.Vendor ?? "";
        if (vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return true;
        if (vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return AmdDiscrete().IsMatch(name);
        if (vendor.Equals("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return IntelDiscrete().IsMatch(name);
        return false;
    }

    /// <summary>"NVIDIA GeForce RTX 4060 Laptop GPU" → "RTX 4060"; "AMD Radeon RX 7800 XT" → "RX 7800 XT".</summary>
    public static string ShortGpuName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var s = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase);
        foreach (var prefix in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel " })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..]; break; }
        foreach (var suffix in new[] { " Laptop GPU", " GPU" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { s = s[..^suffix.Length]; break; }
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
