using System.Globalization;
using System.Text.Json;

namespace Nexus.Monitoring;

/// <summary>
/// Um disco físico como o Windows informa (MSFT_PhysicalDisk) mais os contadores de confiabilidade
/// (MSFT_StorageReliabilityCounter, root\Microsoft\Windows\Storage). Os contadores costumam exigir administrador; sem eles,
/// <see cref="ReliabilityAvailable"/> é falso e só o estado de saúde do Windows conta.
/// </summary>
public sealed record PhysicalDiskReading
{
    public string DeviceId { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    /// <summary>"SSD", "HDD", "SCM" ou "Unspecified".</summary>
    public string MediaType { get; init; } = "";
    /// <summary>"NVMe", "SATA", "USB", "RAID"…</summary>
    public string BusType { get; init; } = "";
    /// <summary>"Healthy", "Warning", "Unhealthy" ou "Unknown".</summary>
    public string HealthStatus { get; init; } = "";
    public string OperationalStatus { get; init; } = "";
    public ulong? SizeBytes { get; init; }
    public bool ReliabilityAvailable { get; init; }
    public double? TemperatureC { get; init; }
    public double? TemperatureMaxC { get; init; }
    /// <summary>% da vida útil nominal já gasta (0 = novo, 100 = fim da vida estimada pelo fabricante).</summary>
    public double? WearPercent { get; init; }
    public ulong? ReadErrorsTotal { get; init; }
    public ulong? ReadErrorsUncorrected { get; init; }
    public ulong? WriteErrorsUncorrected { get; init; }
    public ulong? PowerOnHours { get; init; }

    public bool IsHdd => MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase);
}

public enum DiskHealthLevel
{
    Unknown,
    Ok,
    Attention,
    Critical,
}

public sealed record DiskHealthVerdict(DiskHealthLevel Level, IReadOnlyList<string> Reasons)
{
    public string LevelText => Level switch
    {
        DiskHealthLevel.Ok => "OK",
        DiskHealthLevel.Attention => "Atenção",
        DiskHealthLevel.Critical => "Crítico",
        _ => "Sem dados",
    };
}

/// <summary>
/// Classificação OK / Atenção / Crítico. Critérios (documentados em docs/modules/monitor.md):
/// Crítico — Windows diz "Unhealthy"; desgaste ≥ 90%; qualquer erro de leitura/escrita NÃO corrigido; temperatura ≥ 75 °C
/// (SSD) ou ≥ 60 °C (HDD). Atenção — Windows diz "Warning"; estado operacional diferente de OK; desgaste ≥ 70%;
/// temperatura ≥ 65 °C (SSD) ou ≥ 50 °C (HDD); HDD com ≥ 40.000 horas ligado. Erros de leitura CORRIGIDOS são mostrados mas não
/// rebaixam: a correção (ECC) é o funcionamento normal do disco.
/// </summary>
public static class DiskHealthClassifier
{
    public const double WearCritical = 90, WearAttention = 70;
    public const double SsdTempCritical = 75, SsdTempAttention = 65, HddTempCritical = 60, HddTempAttention = 50;
    public const ulong HddOldHours = 40_000;

    public static DiskHealthVerdict Classify(PhysicalDiskReading d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var critical = new List<string>();
        var attention = new List<string>();
        var pt = CultureInfo.GetCultureInfo("pt-BR");

        switch (d.HealthStatus.Trim().ToUpperInvariant())
        {
            case "UNHEALTHY": critical.Add("O Windows marca o disco como com defeito (Unhealthy). Faça backup agora."); break;
            case "WARNING": attention.Add("O Windows marca o disco com aviso (Warning)."); break;
        }

        var op = d.OperationalStatus.Trim();
        if (op.Length > 0 && !op.Split(',').Any(s => s.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase)))
            attention.Add($"Estado operacional informado: {op}.");

        if (d.WearPercent is { } wear)
        {
            if (wear >= WearCritical) critical.Add($"Desgaste em {wear:0}% da vida útil nominal. Planeje a troca e mantenha backup.");
            else if (wear >= WearAttention) attention.Add($"Desgaste em {wear:0}% da vida útil nominal.");
        }

        var uncorrected = (d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0);
        if (uncorrected > 0) critical.Add($"{uncorrected.ToString("N0", pt)} erro(s) de leitura/escrita não corrigidos: dados podem ter sido perdidos. Faça backup.");

        if (d.TemperatureC is { } t and > 0)
        {
            var (crit, warn) = d.IsHdd ? (HddTempCritical, HddTempAttention) : (SsdTempCritical, SsdTempAttention);
            if (t >= crit) critical.Add($"Temperatura de {t:0} °C: acima do seguro para {(d.IsHdd ? "HD" : "SSD")}. Verifique ventilação/dissipador.");
            else if (t >= warn) attention.Add($"Temperatura de {t:0} °C: quente. Em SSD NVMe o desempenho cai para se proteger.");
        }

        if (d.IsHdd && d.PowerOnHours >= HddOldHours)
            attention.Add($"{d.PowerOnHours.Value.ToString("N0", pt)} horas ligado (mais de 4,5 anos): HDs nessa idade falham mais. Mantenha backup.");

        if (critical.Count > 0) return new(DiskHealthLevel.Critical, [.. critical, .. attention]);
        if (attention.Count > 0) return new(DiskHealthLevel.Attention, attention);

        var known = d.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase);
        if (!known && !d.ReliabilityAvailable) return new(DiskHealthLevel.Unknown, ["O Windows não informou o estado deste disco."]);
        var reasons = new List<string> { known ? "O Windows informa o disco como saudável." : "Nenhum contador fora do normal." };
        if (!d.ReliabilityAvailable)
            reasons.Add("Contadores de confiabilidade (temperatura, desgaste, erros) indisponíveis: o Windows exige administrador ou o controlador não informa.");
        return new(DiskHealthLevel.Ok, reasons);
    }
}

/// <summary>Lê o JSON gerado pelo script de <see cref="DiskHealthScript"/> (objeto único ou lista; campos nulos tolerados).</summary>
public static class DiskHealthParser
{
    public static IReadOnlyList<PhysicalDiskReading> Parse(string? json)
    {
        json = json?.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.EnumerateArray().ToList(),
            JsonValueKind.Object => [doc.RootElement],
            _ => [],
        };
        return [.. items.Where(e => e.ValueKind == JsonValueKind.Object).Select(Read)];
    }

    private static PhysicalDiskReading Read(JsonElement e) => new()
    {
        DeviceId = Str(e, "DeviceId"),
        FriendlyName = Str(e, "FriendlyName"),
        MediaType = Str(e, "MediaType") switch { "3" => "HDD", "4" => "SSD", "5" => "SCM", "0" => "Unspecified", var m => m },
        BusType = Str(e, "BusType") switch { "17" => "NVMe", "11" => "SATA", "7" => "USB", "8" => "RAID", "10" => "SAS", "3" => "ATA", "6" => "Fibre Channel", "1" => "SCSI", var b => b },
        HealthStatus = Str(e, "HealthStatus") switch { "0" => "Healthy", "1" => "Warning", "2" => "Unhealthy", "5" => "Unknown", var h => h },
        OperationalStatus = Str(e, "OperationalStatus"),
        SizeBytes = UNum(e, "Size"),
        ReliabilityAvailable = e.TryGetProperty("ReliabilityAvailable", out var r) && r.ValueKind == JsonValueKind.True,
        TemperatureC = Num(e, "Temperature"),
        TemperatureMaxC = Num(e, "TemperatureMax"),
        WearPercent = Num(e, "Wear"),
        ReadErrorsTotal = UNum(e, "ReadErrorsTotal"),
        ReadErrorsUncorrected = UNum(e, "ReadErrorsUncorrected"),
        WriteErrorsUncorrected = UNum(e, "WriteErrorsUncorrected"),
        PowerOnHours = UNum(e, "PowerOnHours"),
    };

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static ulong? UNum(JsonElement e, string name) =>
        Num(e, name) is { } d && d >= 0 ? (ulong)d : null;
}

public static class DiskHealthScript
{
    /// <summary>
    /// Script de LEITURA (Get-PhysicalDisk + Get-StorageReliabilityCounter, cmdlets do próprio Windows). Não altera nada.
    /// Sem administrador, Get-StorageReliabilityCounter falha em silêncio e ReliabilityAvailable fica falso.
    /// </summary>
    public const string Text =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ErrorActionPreference='SilentlyContinue';" +
        "$l=@(Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter; [pscustomobject]@{" +
        "DeviceId=[string]$_.DeviceId; FriendlyName=[string]$_.FriendlyName; MediaType=[string]$_.MediaType; BusType=[string]$_.BusType;" +
        "HealthStatus=[string]$_.HealthStatus; OperationalStatus=(@($_.OperationalStatus) -join ','); Size=$_.Size;" +
        "ReliabilityAvailable=($null -ne $r); Temperature=$r.Temperature; TemperatureMax=$r.TemperatureMax; Wear=$r.Wear;" +
        "ReadErrorsTotal=$r.ReadErrorsTotal; ReadErrorsUncorrected=$r.ReadErrorsUncorrected; WriteErrorsUncorrected=$r.WriteErrorsUncorrected;" +
        "PowerOnHours=$r.PowerOnHours } });" +
        "ConvertTo-Json -InputObject $l -Compress -Depth 3";
}
