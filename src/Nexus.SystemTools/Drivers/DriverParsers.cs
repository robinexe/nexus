using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.SystemTools.Drivers;

/// <summary>Pacote de driver de terceiros no repositório de drivers do Windows (driver store).</summary>
public sealed record DriverPackage(
    string PublishedName,
    string OriginalName,
    string Provider,
    string ClassName,
    string? ClassGuid,
    string VersionText,
    Version? Version,
    DateOnly? Date,
    string Signer)
{
    /// <summary>Identidade do pacote independente do nome publicado (oemNN.inf muda ao reinstalar).</summary>
    public string Identity => $"{OriginalName.ToLowerInvariant()}|{Provider.ToLowerInvariant()}|{ClassGuid?.ToLowerInvariant()}";
}

/// <summary>Leitura de <c>pnputil /enum-drivers</c> e <c>pnputil /enum-devices</c> em português e inglês.</summary>
public static partial class DriverParsers
{
    [GeneratedRegex(@"^oem[0-9]{1,5}\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OemInf();

    [GeneratedRegex(@"^\{[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}$")]
    private static partial Regex GuidValue();

    [GeneratedRegex(@"^(?<a>\d{1,2})[/.\-](?<b>\d{1,2})[/.\-](?<y>\d{4})\s+(?<v>\d+(\.\d+){1,3})\s*$")]
    private static partial Regex DateVersion();

    public static bool IsOemInf(string? name) => name is not null && OemInf().IsMatch(name);

    public static IReadOnlyList<DriverPackage> ParseEnumDrivers(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var lines = Text.Lines(output).ToList();
        var portuguese = lines.Any(l => Text.Fold(l).Contains("publicado", StringComparison.Ordinal));
        var result = new List<DriverPackage>();
        Dictionary<string, string>? cur = null;

        void Flush()
        {
            if (cur is null || !cur.TryGetValue("published", out var pub)) return;
            cur.TryGetValue("dateversion", out var dv);
            Version? version = null;
            DateOnly? date = null;
            var vtext = dv ?? "";
            if (dv is not null && DateVersion().Match(dv) is { Success: true } m)
            {
                if (!Version.TryParse(m.Groups["v"].Value, out version)) version = null;
                vtext = m.Groups["v"].Value;
                date = ParseDate(int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), portuguese);
            }
            result.Add(new DriverPackage(pub, cur.GetValueOrDefault("original") ?? "", cur.GetValueOrDefault("provider") ?? "",
                cur.GetValueOrDefault("class") ?? "", cur.GetValueOrDefault("guid"), vtext, version, date, cur.GetValueOrDefault("signer") ?? ""));
        }

        foreach (var line in lines)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var label = Text.Fold(line[..colon]).Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0) continue;

            if (IsOemInf(value) && (label.Contains("publi", StringComparison.Ordinal) || cur is null || cur.ContainsKey("published")))
            {
                Flush();
                cur = new Dictionary<string, string>(StringComparer.Ordinal) { ["published"] = value.ToLowerInvariant() };
                continue;
            }
            if (cur is null) continue;
            if (GuidValue().IsMatch(value))
            {
                if (label.Contains("clas", StringComparison.Ordinal)) cur.TryAdd("guid", value.ToLowerInvariant());
                continue; // Extension ID e outros GUIDs
            }
            if (DateVersion().IsMatch(value)) { cur.TryAdd("dateversion", value); continue; }
            if (label.Contains("original", StringComparison.Ordinal)) cur.TryAdd("original", value);
            else if (label.Contains("provider", StringComparison.Ordinal) || label.Contains("provedor", StringComparison.Ordinal) ||
                     label.Contains("fornecedor", StringComparison.Ordinal) || label.Contains("fabricante", StringComparison.Ordinal)) cur.TryAdd("provider", value);
            else if (label.Contains("sign", StringComparison.Ordinal) || label.Contains("assin", StringComparison.Ordinal)) cur.TryAdd("signer", value);
            else if (label.Contains("clas", StringComparison.Ordinal) && !label.Contains("vers", StringComparison.Ordinal)) cur.TryAdd("class", value);
        }
        Flush();
        return result;
    }

    private static DateOnly? ParseDate(int a, int b, int year, bool dayFirst)
    {
        int day, month;
        if (a > 12) (day, month) = (a, b);
        else if (b > 12) (month, day) = (a, b);
        else if (dayFirst) (day, month) = (a, b);
        else (month, day) = (a, b);
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Pacotes em uso segundo <c>pnputil /enum-devices</c>: nome publicado → descrições dos dispositivos. null quando a saída não
    /// é reconhecível (ex.: Windows anterior ao 10 2004, sem /enum-devices) — nesse caso nada pode ser removido.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? ParseDevicesInUse(string output, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (exitCode != 0) return null;
        var lines = Text.Lines(output).ToList();
        // Saída válida: tem ao menos um bloco de dispositivo (ID da instância).
        if (!lines.Any(l => { var f = Text.Fold(l); return f.Contains("instance id", StringComparison.Ordinal) || f.Contains("id da instancia", StringComparison.Ordinal); }))
            return null;
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string description = "";
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) { description = ""; continue; }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var label = Text.Fold(line[..colon]);
            var value = line[(colon + 1)..].Trim();
            if (label.Contains("instance id", StringComparison.Ordinal) || label.Contains("id da instancia", StringComparison.Ordinal)) description = "";
            else if (label.Contains("descri", StringComparison.Ordinal)) description = value;
            else if (IsOemInf(value))
            {
                if (!map.TryGetValue(value, out var l)) map[value] = l = [];
                if (description.Length > 0 && !l.Contains(description)) l.Add(description);
            }
        }
        return map.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Regras de proteção: o que nunca é removido do repositório de drivers.</summary>
public static class DriverRules
{
    // Classes de dispositivo críticas para iniciar o Windows ou usar o PC (GUIDs de classe documentados pela Microsoft).
    public static readonly IReadOnlyDictionary<string, string> CriticalClasses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["{4d36e967-e325-11ce-bfc1-08002be10318}"] = "unidades de disco",
        ["{4d36e96a-e325-11ce-bfc1-08002be10318}"] = "controladoras IDE/SATA",
        ["{4d36e97b-e325-11ce-bfc1-08002be10318}"] = "controladoras de armazenamento",
        ["{71a27cdd-812a-11d0-bec7-08002be2092f}"] = "volumes",
        ["{4d36e97d-e325-11ce-bfc1-08002be10318}"] = "dispositivos do sistema",
        ["{50127dc3-0f36-415e-a6cc-4cb3be910b65}"] = "processadores",
        ["{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}"] = "firmware",
        ["{4d36e96b-e325-11ce-bfc1-08002be10318}"] = "teclados",
        ["{36fc9e60-c465-11cf-8056-444553540000}"] = "controladoras USB",
        ["{d48179be-ec20-11d1-b6b8-00c04fa372a7}"] = "segurança do sistema (SBP2)",
    };

    public const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    public const string NetClass = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Existe outra versão MAIS NOVA do mesmo pacote (mesmo INF original, fornecedor e classe) no repositório.</summary>
    public static DriverPackage? NewerDuplicate(DriverPackage pkg, IEnumerable<DriverPackage> all)
    {
        ArgumentNullException.ThrowIfNull(pkg);
        return all.Where(o => o.PublishedName != pkg.PublishedName && o.Identity == pkg.Identity && Compare(o, pkg) > 0)
            .OrderByDescending(o => o.Version).ThenByDescending(o => o.Date).FirstOrDefault();
    }

    private static int Compare(DriverPackage a, DriverPackage b)
    {
        if (a.Version is not null && b.Version is not null && a.Version != b.Version) return a.Version.CompareTo(b.Version);
        if (a.Date is { } da && b.Date is { } db && da != db) return da.CompareTo(db);
        return 0;
    }

    /// <summary>Motivo para NÃO remover, ou null se a remoção é permitida.</summary>
    public static string? CheckRemoval(DriverPackage pkg, IReadOnlyList<DriverPackage> all, IReadOnlyDictionary<string, IReadOnlyList<string>>? inUse)
    {
        ArgumentNullException.ThrowIfNull(pkg);
        ArgumentNullException.ThrowIfNull(all);
        if (!DriverParsers.IsOemInf(pkg.PublishedName)) return "Só pacotes de terceiros (oemNN.inf) podem ser removidos.";
        if (inUse is null) return "Não foi possível confirmar se o driver está em uso (requer Windows 10 versão 2004 ou mais novo).";
        if (inUse.TryGetValue(pkg.PublishedName, out var devices))
            return devices.Count > 0 ? $"Em uso por: {string.Join(", ", devices.Take(3))}." : "Em uso por um dispositivo.";
        if (pkg.ClassGuid is null) return "Classe do driver desconhecida: não é removido.";
        if (CriticalClasses.TryGetValue(pkg.ClassGuid, out var cls)) return $"Classe crítica ({cls}): não é removido.";
        if ((string.Equals(pkg.ClassGuid, DisplayClass, StringComparison.OrdinalIgnoreCase) || string.Equals(pkg.ClassGuid, NetClass, StringComparison.OrdinalIgnoreCase))
            && NewerDuplicate(pkg, all) is null)
            return "Driver de vídeo ou rede: só versões antigas com uma versão mais nova instalada podem ser removidas.";
        return null;
    }
}
