using System.Text;
using Nexus.Automation;
using Nexus.SelfTest;

namespace Nexus.Cli;

/// <summary>
/// <c>nexus selftest</c>: roda as leituras de todos os módulos no PC real e grava o relatório pedido (--json/--html).
/// As únicas escritas são os arquivos de relatório que o usuário indicou; o sistema não é alterado.
/// </summary>
internal static class SelfTestCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var parsed = SelfTestArguments.Parse(args, out var error);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return CliExit.Usage;
        }
        if (parsed.Help)
        {
            Console.WriteLine(SelfTestArguments.Usage);
            return CliExit.Ok;
        }

        UiSelfTestReport? ui = null;
        if (parsed.UiReportPath is { } uiPath)
        {
            if (!File.Exists(uiPath)) Console.Error.WriteLine($"Aviso: {uiPath} não existe; o HTML sai sem a parte da interface.");
            else if ((ui = UiSelfTestReport.FromJson(await File.ReadAllTextAsync(uiPath, Encoding.UTF8).ConfigureAwait(false))) is null)
                Console.Error.WriteLine($"Aviso: {uiPath} não é um relatório de interface válido; o HTML sai sem essa parte.");
        }

        var options = parsed.Options with { CatalogDirectory = Path.Combine(AppContext.BaseDirectory, "catalog") };
        if (!parsed.Quiet)
        {
            Console.WriteLine("NEXUS · autoteste no PC real (somente leitura: nada é alterado)");
            Console.WriteLine();
        }
        var report = await SelfTestRunner.RunAsync(NexusChecks.All(), options, r => { if (!parsed.Quiet) Console.WriteLine(SelfTestReport.Line(r)); }).ConfigureAwait(false);

        if (!parsed.Quiet)
        {
            Console.WriteLine();
            foreach (var f in report.Results.Where(r => r.Status == CheckStatus.Failure && r.Error is not null))
            {
                Console.WriteLine($"FALHA em {f.Module} · {f.Name}:");
                Console.WriteLine(f.Error);
                Console.WriteLine();
            }
        }
        Console.WriteLine(report.Footer());

        if (parsed.JsonPath is { } json) Save(json, report.ToJson());
        if (parsed.HtmlPath is { } html)
        {
            string? shots = null;
            if (ui is not null && parsed.UiReportPath is { } u)
            {
                var uiDir = Path.GetDirectoryName(Path.GetFullPath(u))!;
                var htmlDir = Path.GetDirectoryName(Path.GetFullPath(html))!;
                shots = Path.GetRelativePath(htmlDir, uiDir).Replace('\\', '/');
                if (shots == ".") shots = "";
            }
            Save(html, report.ToHtml(ui, shots));
        }
        var exit = report.ExitCode;
        if (ui is not null && ui.ExitCode != 0) exit = CliExit.Partial;
        return exit;
    }

    private static void Save(string path, string content)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        Console.WriteLine($"Relatório: {full}");
    }
}
