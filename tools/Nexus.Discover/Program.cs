using Nexus.HardwareDiscovery;

// Uso: Nexus.Discover [--rows N]   (padrão: 15 linhas por tabela)
var maxRows = args.Length == 2 && args[0] == "--rows" && int.TryParse(args[1], out var n) ? n : 15;
var s = Discovery.Capture();
Console.WriteLine($"NEXUS descoberta (somente leitura) — {s.TakenAt:yyyy-MM-dd HH:mm:ss} — {s.Elapsed.TotalSeconds:0.00} s");
foreach (var section in s.Sections)
{
    Console.WriteLine();
    Console.WriteLine($"== {section.Title}");
    foreach (var f in section.Facts)
    {
        Console.WriteLine($"  [{Tag(f.Status)}] {f.Label}: {f.Value ?? "—"}");
        Console.WriteLine($"         fonte: {f.Source}{(f.Note is null ? "" : $" | {f.Note}")}");
    }
    foreach (var t in section.Tables)
    {
        Console.WriteLine($"  -- {t.Title} ({t.Rows.Count} linhas) [{Tag(t.Status)}] fonte: {t.Source}");
        Console.WriteLine($"     {string.Join(" | ", t.Columns)}");
        foreach (var r in t.Rows.Take(maxRows)) Console.WriteLine($"     {string.Join(" | ", r.Select(c => c ?? "—"))}");
        if (t.Rows.Count > maxRows) Console.WriteLine($"     … mais {t.Rows.Count - maxRows} linha(s)");
    }
}
Console.WriteLine();
Console.WriteLine("== Diagnóstico");
foreach (var d in DiagnosticRules.Evaluate(s.Facts))
    Console.WriteLine($"  [{d.Severity}/{Tag(d.Basis)}] {d.Title} — {d.Evidence}{(d.RelatedActionId is null ? "" : $" (ação: {d.RelatedActionId})")}");
var unavailable = s.Sections.SelectMany(x => x.Facts).Count(f => f.Status == FactStatus.Unavailable);
Console.WriteLine();
Console.WriteLine($"Valores indisponíveis: {unavailable}");
return 0;

static string Tag(FactStatus st) => st switch { FactStatus.Detected => "detectado", FactStatus.Inferred => "inferido", _ => "indisponível" };
