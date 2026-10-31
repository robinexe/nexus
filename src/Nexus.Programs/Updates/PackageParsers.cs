using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Programs.Updates;

public enum PackageManagerKind { Winget, Chocolatey, Scoop, Npm }

/// <summary>Uma atualização disponível, como o gerenciador informou.</summary>
/// <param name="RequiresExplicit">winget: pacote fixado/que exige pedido explícito (aparece numa segunda tabela).</param>
/// <param name="IdTruncated">O winget cortou o ID com "…" (janela estreita): não dá para pedir a atualização por ID.</param>
public sealed record PackageUpdate(
    PackageManagerKind Manager,
    string Name,
    string Id,
    string Current,
    string Available,
    string? Source = null,
    bool RequiresExplicit = false,
    bool IdTruncated = false,
    bool Pinned = false)
{
    /// <summary>Pode ser pedida pelo NEXUS (ID íntegro e válido para o gerenciador, e com origem conhecida no winget).</summary>
    public bool CanUpgrade => !IdTruncated && !Pinned && PackageIdRules.IsValid(Manager, Id) &&
                              (Manager != PackageManagerKind.Winget || !string.IsNullOrWhiteSpace(Source));
}

/// <summary>Formato dos IDs aceitos para montar a linha de atualização (nada fora disso chega ao gerenciador).</summary>
public static partial class PackageIdRules
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_\-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex WingetId();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChocoId();

    [GeneratedRegex(@"^(?:[A-Za-z0-9][A-Za-z0-9._\-]{0,63}/)?[A-Za-z0-9][A-Za-z0-9._\-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ScoopName();

    [GeneratedRegex(@"^(?:@[a-z0-9][a-z0-9._~\-]{0,100}/)?[a-z0-9][a-z0-9._~\-]{0,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NpmName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex WingetSource();

    public static bool IsValid(PackageManagerKind kind, string? id) => id is not null && kind switch
    {
        PackageManagerKind.Winget => WingetId().IsMatch(id),
        PackageManagerKind.Chocolatey => ChocoId().IsMatch(id),
        PackageManagerKind.Scoop => ScoopName().IsMatch(id),
        PackageManagerKind.Npm => NpmName().IsMatch(id),
        _ => false,
    };

    public static bool IsValidWingetSource(string? source) => source is not null && WingetSource().IsMatch(source);
}

/// <summary>
/// Tabela de largura fixa (winget e Format-Table do PowerShell). Colunas pelos segmentos da linha de traços quando ela tem
/// separações (scoop), senão pelo início das palavras do cabeçalho (winget, cuja linha de traços é contínua). Larguras em
/// colunas de exibição: caracteres asiáticos largos ocupam 2, como no console.
/// </summary>
public static class FixedWidthTable
{
    public static bool IsDashLine(string line)
    {
        var t = line.Trim();
        return t.Length >= 4 && t.All(c => c is '-' or ' ' or '─') && t.Count(c => c is '-' or '─') >= 4;
    }

    public static int CharWidth(char c) =>
        c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣' or >= '豈' and <= '﫿' or
            >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦'
            ? 2 : 1;

    /// <summary>Colunas de início (em largura de exibição).</summary>
    public static List<int> ColumnStarts(string header, string dashes)
    {
        var segments = new List<int>();
        for (var i = 0; i < dashes.Length; i++)
            if (dashes[i] is '-' or '─' && (i == 0 || dashes[i - 1] == ' ')) segments.Add(DisplayColumn(dashes, i));
        if (segments.Count >= 2) return segments;
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(DisplayColumn(header, i));
        return starts;
    }

    private static int DisplayColumn(string s, int index)
    {
        var col = 0;
        for (var i = 0; i < index && i < s.Length; i++) col += CharWidth(s[i]);
        return col;
    }

    /// <summary>Recorta a linha nas colunas (largura de exibição) e apara cada campo.</summary>
    public static List<string> Slice(string line, IReadOnlyList<int> starts)
    {
        var fields = new List<string>(starts.Count);
        var col = 0;
        var idx = 0;
        for (var c = 0; c < starts.Count; c++)
        {
            while (idx < line.Length && col < starts[c]) { col += CharWidth(line[idx]); idx++; }
            var begin = idx;
            var end = c + 1 < starts.Count ? starts[c + 1] : int.MaxValue;
            var j = idx;
            var cc = col;
            while (j < line.Length && cc < end) { cc += CharWidth(line[j]); j++; }
            fields.Add(line[begin..j].Trim());
            idx = j;
            col = cc;
        }
        return fields;
    }

    /// <summary>Linhas como o console as mostraria: só o que vem depois do último '\r' (barras de progresso se sobrescrevem).</summary>
    public static List<string> CleanLines(string output) =>
        (output ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿').Split('\n')
            .Select(l => { l = l.TrimEnd('\r'); var r = l.LastIndexOf('\r'); return (r >= 0 ? l[(r + 1)..] : l).TrimEnd(); })
            .Select(StripAnsi).ToList();

    private static string StripAnsi(string s) => s.Contains('\u001b', StringComparison.Ordinal) ? Regex.Replace(s, @"\u001b\[[0-9;?]*[A-Za-z]", "") : s;
}

/// <summary>Saída de <c>winget upgrade --include-unknown</c> (inglês, português ou outro idioma: colunas pela posição).</summary>
public static class WingetParser
{
    public const char Ellipsis = '…';

    public static IReadOnlyList<PackageUpdate> ParseUpgradeTable(string output)
    {
        var lines = FixedWidthTable.CleanLines(output);
        var list = new List<PackageUpdate>();
        var explicitSection = false;
        var tableEnd = 0;
        for (var i = 1; i < lines.Count; i++)
        {
            if (!FixedWidthTable.IsDashLine(lines[i]) || lines[i - 1].Trim().Length == 0) continue;
            // Texto entre a tabela anterior e este cabeçalho: "...require explicit targeting..." (segunda tabela).
            for (var k = tableEnd; k < i - 1; k++)
                if (IsExplicitBanner(lines[k])) explicitSection = true;
            var starts = FixedWidthTable.ColumnStarts(lines[i - 1], lines[i]);
            if (starts.Count < 4) continue;
            var j = i + 1;
            for (; j < lines.Count; j++)
            {
                var line = lines[j];
                if (line.Trim().Length == 0) break;
                var f = FixedWidthTable.Slice(line, starts);
                if (f.Count < 4 || f[1].Length == 0 || f[2].Length == 0 || f[3].Length == 0) break;
                if (f[1].Contains(' ', StringComparison.Ordinal)) continue; // não é um ID (texto que transbordou para a coluna)
                list.Add(new PackageUpdate(PackageManagerKind.Winget, f[0], f[1], f[2], f[3], f.Count > 4 && f[4].Length > 0 ? f[4] : null,
                    RequiresExplicit: explicitSection, IdTruncated: f[1].EndsWith(Ellipsis)));
            }
            tableEnd = j;
            i = j;
        }
        return list;
    }

    private static bool IsExplicitBanner(string line) =>
        line.Contains("explicit", StringComparison.OrdinalIgnoreCase) || line.Contains("explícit", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("pinned", StringComparison.OrdinalIgnoreCase) || line.Contains("fixad", StringComparison.OrdinalIgnoreCase);

    /// <summary>A saída pede para aceitar os termos de uma origem (sem entrada, o winget não continua).</summary>
    public static bool NeedsSourceAgreement(string output) =>
        (output ?? "").Contains("agreement", StringComparison.OrdinalIgnoreCase) || (output ?? "").Contains("contrato", StringComparison.OrdinalIgnoreCase) ||
        (output ?? "").Contains("Termos de Transa", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// JSON de <c>Get-WinGetPackage</c> (módulo Microsoft.WinGet.Client), projetado pelo NEXUS em Name/Id/InstalledVersion/
    /// AvailableVersion/Source. Usado quando a tabela não pôde ser lida (o winget não tem saída JSON própria para upgrade).
    /// </summary>
    public static IReadOnlyList<PackageUpdate> ParseClientJson(string json)
    {
        var text = (json ?? "").Trim().TrimStart('﻿');
        if (text.Length == 0) return [];
        using var doc = JsonDocument.Parse(text);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var list = new List<PackageUpdate>();
        foreach (var e in items)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = Str(e, "Id");
            var available = Str(e, "AvailableVersion");
            if (id is null || available is null) continue;
            list.Add(new PackageUpdate(PackageManagerKind.Winget, Str(e, "Name") ?? id, id, Str(e, "InstalledVersion") ?? "?", available, Str(e, "Source")));
        }
        return list;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString()!.Trim() : null;
}

/// <summary>Saída de <c>choco outdated -r</c>: <c>nome|atual|disponível|fixado</c> por linha.</summary>
public static class ChocolateyParser
{
    public static IReadOnlyList<PackageUpdate> ParseOutdated(string output)
    {
        var list = new List<PackageUpdate>();
        foreach (var line in FixedWidthTable.CleanLines(output))
        {
            var f = line.Split('|');
            if (f.Length != 4 || f[0].Trim().Length == 0 || f[1].Trim().Length == 0 || f[2].Trim().Length == 0) continue;
            var name = f[0].Trim();
            if (name.Contains(' ', StringComparison.Ordinal)) continue;
            list.Add(new PackageUpdate(PackageManagerKind.Chocolatey, name, name, f[1].Trim(), f[2].Trim(), "chocolatey",
                Pinned: f[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }
}

/// <summary>
/// Saída de <c>scoop status</c>: tabela (versões novas, colunas Name / Installed Version / Latest Version / Missing
/// Dependencies / Info) ou o formato antigo ("Updates are available for:" + <c>app: 1.0 -> 2.0</c>).
/// </summary>
public static partial class ScoopParser
{
    [GeneratedRegex(@"^\s*([A-Za-z0-9][A-Za-z0-9._/\-]*):\s+(\S+)\s+->\s+(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyLine();

    public static IReadOnlyList<PackageUpdate> ParseStatus(string output)
    {
        var lines = FixedWidthTable.CleanLines(output);
        var list = new List<PackageUpdate>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (!FixedWidthTable.IsDashLine(lines[i]) || lines[i - 1].Trim().Length == 0) continue;
            var starts = FixedWidthTable.ColumnStarts(lines[i - 1], lines[i]);
            if (starts.Count < 3) continue;
            for (var j = i + 1; j < lines.Count && lines[j].Trim().Length > 0; j++)
            {
                var f = FixedWidthTable.Slice(lines[j], starts);
                if (f[0].Length == 0 || f[0].Contains(' ', StringComparison.Ordinal)) break;
                var info = f.Count > 4 ? f[4] : "";
                var latest = f[2];
                if (latest.Length == 0 || latest == f[1]) continue; // falhou ao instalar / sem versão nova
                list.Add(new PackageUpdate(PackageManagerKind.Scoop, f[0], f[0], f[1], latest, "scoop",
                    Pinned: info.Contains("Held", StringComparison.OrdinalIgnoreCase)));
            }
            return list;
        }
        foreach (var line in lines)
            if (LegacyLine().Match(line) is { Success: true } m)
                list.Add(new PackageUpdate(PackageManagerKind.Scoop, m.Groups[1].Value, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, "scoop"));
        return list;
    }

    /// <summary>O próprio Scoop avisou que está desatualizado (a lista pode estar incompleta).</summary>
    public static bool ScoopOutOfDate(string output) => (output ?? "").Contains("Scoop out of date", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Saída de <c>npm outdated -g --json</c> (objeto por pacote: current/wanted/latest). Código de saída 1 é normal aqui.</summary>
public static class NpmParser
{
    public static IReadOnlyList<PackageUpdate> ParseOutdated(string json)
    {
        var text = (json ?? "").Trim().TrimStart('﻿');
        if (text.Length == 0) return [];
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
        var list = new List<PackageUpdate>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            var current = WingetParser.Str(p.Value, "current");
            var latest = WingetParser.Str(p.Value, "latest") ?? WingetParser.Str(p.Value, "wanted");
            if (current is null || latest is null || current == latest) continue;
            list.Add(new PackageUpdate(PackageManagerKind.Npm, p.Name, p.Name, current, latest, "npm"));
        }
        return list.OrderBy(u => u.Name, StringComparer.Ordinal).ToList();
    }
}
