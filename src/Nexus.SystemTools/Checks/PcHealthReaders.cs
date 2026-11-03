using System.Globalization;
using System.Text.Json;
using Nexus.HardwareDiscovery;

namespace Nexus.SystemTools.Checks;

// =====================================================================================================================
// Leitores (somente leitura) da saúde dos discos e da limitação do processador pelo firmware. Ambos usam o PowerShell da
// Microsoft (oculto, sem perfil) com consultas CIM/log de eventos e devolvem JSON; a leitura do JSON é pura e testada.
// =====================================================================================================================

/// <summary>Saúde dos discos: MSFT_PhysicalDisk + MSFT_StorageReliabilityCounter (namespace root/Microsoft/Windows/Storage).</summary>
public static class DiskHealthReader
{
    /// <summary>
    /// Script de leitura. Os contadores de confiabilidade muitas vezes exigem administrador ou vêm vazios: nesse caso o disco
    /// aparece com "Rel = false" e os campos ficam "não informado" na tela (nunca zero inventado).
    /// </summary>
    public const string Script =
        "$ns='root/Microsoft/Windows/Storage';$r=@();" +
        "foreach($d in @(Get-CimInstance -Namespace $ns -ClassName MSFT_PhysicalDisk -ErrorAction Stop)){" +
        "$o=[ordered]@{DeviceId=[string]$d.DeviceId;FriendlyName=[string]$d.FriendlyName;MediaType=[int]$d.MediaType;BusType=[int]$d.BusType;" +
        "HealthStatus=[int]$d.HealthStatus;OperationalStatus=@($d.OperationalStatus|ForEach-Object{[int]$_});Size=[uint64]$d.Size;Rel=$false};" +
        "try{$c=Get-CimAssociatedInstance -InputObject $d -ResultClassName MSFT_StorageReliabilityCounter -ErrorAction Stop|Select-Object -First 1;" +
        "if($c){$o.Rel=$true;$o.Temperature=$c.Temperature;$o.TemperatureMax=$c.TemperatureMax;$o.Wear=$c.Wear;" +
        "$o.ReadErrorsUncorrected=$c.ReadErrorsUncorrected;$o.WriteErrorsUncorrected=$c.WriteErrorsUncorrected;$o.PowerOnHours=$c.PowerOnHours}}" +
        "catch{$o.RelError=$_.Exception.Message};$r+=[pscustomobject]$o};" +
        "ConvertTo-Json -InputObject @($r) -Depth 4 -Compress";

    public static IReadOnlyList<DiskHealthFacts> Read(IProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var result = PowerShellRunner.Run(runner, Script, TimeSpan.FromSeconds(45));
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output)) throw new InvalidOperationException(FirstLine(result.Error));
        return Parse(result.Output);
    }

    /// <summary>Lê o JSON do script (lista ou objeto único). JSON inválido = lista vazia.</summary>
    public static IReadOnlyList<DiskHealthFacts> Parse(string? json)
    {
        var list = new List<DiskHealthFacts>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            foreach (var e in items.Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var rel = Bool(e, "Rel") == true;
                var temp = Int(e, "Temperature");
                list.Add(new DiskHealthFacts(
                    int.TryParse(Str(e, "DeviceId"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null,
                    Str(e, "FriendlyName") ?? "",
                    Media(Int(e, "MediaType")),
                    StorageClassifier.BusName(Int(e, "BusType")),
                    Health(Int(e, "HealthStatus")),
                    e.TryGetProperty("OperationalStatus", out var os) && os.ValueKind == JsonValueKind.Array
                        ? [.. os.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => Operational(x.GetInt32()))]
                        : [],
                    TemperatureC: temp is > 0 and < 150 ? temp : null, // 0 = o disco não informa
                    TemperatureMaxC: Int(e, "TemperatureMax") is > 0 and < 150 and var tm ? tm : null,
                    WearPercent: rel && Int(e, "Wear") is >= 0 and <= 100 and var w ? w : null,
                    ReadErrorsUncorrected: rel ? Long(e, "ReadErrorsUncorrected") : null,
                    WriteErrorsUncorrected: rel ? Long(e, "WriteErrorsUncorrected") : null,
                    PowerOnHours: rel && Long(e, "PowerOnHours") is > 0 and var h ? h : null,
                    ReliabilityRead: rel,
                    SizeBytes: Long(e, "Size") is > 0 and var size ? (ulong)size : null));
            }
        }
        catch (JsonException) { list.Clear(); }
        return list;
    }

    /// <summary>MSFT_PhysicalDisk.MediaType: 3 = HDD, 4 = SSD, 5 = SCM, 0 = não especificado.</summary>
    public static DiskMedia Media(int? mediaType) => mediaType switch { 3 => DiskMedia.Hdd, 4 => DiskMedia.Ssd, 5 => DiskMedia.Scm, _ => DiskMedia.Unknown };

    /// <summary>MSFT_PhysicalDisk.HealthStatus: 0 = Healthy, 1 = Warning, 2 = Unhealthy, 5 = Unknown.</summary>
    public static string? Health(int? status) => status switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", 5 => "Unknown", _ => null };

    /// <summary>MSFT_PhysicalDisk.OperationalStatus em palavras (só os comuns; o resto vira "código N").</summary>
    public static string Operational(int code) => code switch
    {
        2 => "OK",
        3 => "Degradado",
        5 => "Falha prevista",
        6 => "Erro",
        7 => "Erro não recuperável",
        10 => "Parado",
        13 => "Sem comunicação",
        _ => "código " + code.ToString(CultureInfo.InvariantCulture),
    };

    internal static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    internal static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
    internal static double? Double(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    internal static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    internal static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "PowerShell falhou.";
}

/// <summary>
/// Limitação do processador pelo firmware: eventos 37 do Kernel-Processor-Power no log Sistema (sem administrador) e o
/// contador "% Performance Limit" (classe WMI Win32_PerfFormattedData_Counters_ProcessorInformation: o nome não muda com o
/// idioma do Windows, ao contrário do caminho do contador).
/// </summary>
public static class ThrottleReader
{
    public const int WindowDays = 7;

    public const string Script =
        "$o=[ordered]@{};" +
        "try{$e=@(Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Processor-Power';Id=37;StartTime=(Get-Date).AddDays(-7)} -MaxEvents 1000 -ErrorAction Stop);" +
        "$o.Count=$e.Count;if($e.Count){$o.Last=$e[0].TimeCreated.ToUniversalTime().ToString('o')}}" +
        "catch{if($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*'){$o.Count=0}else{$o.EventError=$_.Exception.Message}};" +
        "$v=@();for($i=0;$i -lt 3;$i++){try{$p=Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ProcessorInformation -Filter \"Name='_Total'\" -ErrorAction Stop;" +
        "if($p -and $null -ne $p.PercentPerformanceLimit){$v+=[double]$p.PercentPerformanceLimit}}catch{};Start-Sleep -Milliseconds 400};" +
        "if($v.Count){$o.Limit=($v|Measure-Object -Minimum).Minimum};" +
        "ConvertTo-Json -InputObject ([pscustomobject]$o) -Compress";

    public static ThrottleFacts Read(IProcessRunner runner, bool? onBattery)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var result = PowerShellRunner.Run(runner, Script, TimeSpan.FromSeconds(45));
        return Parse(result.Output, onBattery);
    }

    public static ThrottleFacts Parse(string? json, bool? onBattery)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ThrottleFacts(null, null, null, onBattery, WindowDays);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return new ThrottleFacts(null, null, null, onBattery, WindowDays);
            DateTimeOffset? last = DateTimeOffset.TryParse(DiskHealthReader.Str(e, "Last"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var l) ? l : null;
            var limit = DiskHealthReader.Double(e, "Limit");
            return new ThrottleFacts(DiskHealthReader.Int(e, "Count"), last, limit is >= 0 and <= 1000 ? limit : null, onBattery, WindowDays);
        }
        catch (JsonException) { return new ThrottleFacts(null, null, null, onBattery, WindowDays); }
    }
}
