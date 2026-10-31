using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Security;

public enum ScanKind { Quick = 1, Full = 2, Custom = 3 }

/// <summary>Resultado de uma execução do MpCmdRun (a ferramenta de linha de comando do próprio Defender).</summary>
public sealed record DefenderRunResult(bool Started, int? ExitCode, TimeSpan Elapsed, string Summary, IReadOnlyList<string> Output, bool Cancelled);

/// <summary>
/// Interpretação pura da saída e do código de saída do MpCmdRun (documentado em
/// https://learn.microsoft.com/defender-endpoint/command-line-arguments-microsoft-defender-antivirus).
/// O MpCmdRun não informa porcentagem: a interface mostra tempo decorrido e as linhas que ele escreve.
/// </summary>
public static partial class DefenderOutput
{
    [GeneratedRegex(@"found\s+(\d+)\s+threats?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FoundPattern();

    public static string Summarize(bool update, int? exitCode, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var all = string.Join("\n", lines);
        if (update)
        {
            if (exitCode == 0) return "Assinaturas atualizadas (ou já estavam em dia).";
            if (all.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) || all.Contains("access", StringComparison.OrdinalIgnoreCase))
                return "O Windows recusou a atualização sem administrador. Use o Windows Update ou a Segurança do Windows > Atualizações de proteção.";
            return $"A atualização não terminou (código {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}). Sem internet, o Defender não atualiza; tente pelo Windows Update.";
        }
        var m = FoundPattern().Match(all);
        if (all.Contains("found no threats", StringComparison.OrdinalIgnoreCase)) return "Verificação concluída: nenhuma ameaça encontrada.";
        if (m.Success && m.Groups[1].Value != "0")
            return $"Verificação concluída: {m.Groups[1].Value} ameaça(s) encontrada(s). Veja o histórico abaixo e trate pela Segurança do Windows.";
        if (exitCode == 2) return "Verificação concluída: o Defender encontrou ameaça(s). Veja o histórico abaixo e trate pela Segurança do Windows.";
        if (exitCode == 0) return "Verificação concluída: nenhuma ameaça encontrada.";
        if (all.Contains("0x80508023", StringComparison.OrdinalIgnoreCase)) return "O Defender não está ativo (outro antivírus assumiu). Use a verificação do seu antivírus.";
        if (all.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)) return "Acesso negado pelo Windows. Rode a verificação pela Segurança do Windows.";
        return $"A verificação terminou com código {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}. Confira o histórico na Segurança do Windows.";
    }
}

/// <summary>
/// Exceção controlada do brief: roda o MpCmdRun da Microsoft (verificação rápida/completa/personalizada, atualização de
/// assinaturas) depois de confirmação na interface. Não muda nenhuma configuração; remoção/quarentena continuam com o Defender.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DefenderScanner
{
    /// <summary>MpCmdRun da plataforma mais nova (ProgramData\...\Platform\&lt;versão&gt;) ou o de Program Files.</summary>
    public static string? FindMpCmdRun()
    {
        try
        {
            var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
            if (Directory.Exists(platform))
            {
                var newest = Directory.EnumerateDirectories(platform)
                    .Select(d => (Dir: d, Ver: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                    .Where(x => x.Ver is not null && File.Exists(Path.Combine(x.Dir, "MpCmdRun.exe")))
                    .OrderByDescending(x => x.Ver).FirstOrDefault();
                if (newest.Dir is not null) return Path.Combine(newest.Dir, "MpCmdRun.exe");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        return File.Exists(pf) ? pf : null;
    }

    /// <summary>Pasta aceita para verificação personalizada: absoluta, existente, sem link (reparse point).</summary>
    public static string? ValidateCustomPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260 || !Path.IsPathFullyQualified(path)) return null;
        if (path.IndexOfAny(['"', '\0', '\n', '\r']) >= 0) return null;
        if (File.Exists(path)) return new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint) ? null : path;
        if (Directory.Exists(path)) return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint) ? null : path;
        return null;
    }

    public static Task<DefenderRunResult> ScanAsync(ScanKind kind, string? customPath, Action<string, TimeSpan> progress, CancellationToken cancel)
    {
        var args = new List<string> { "-Scan", "-ScanType", ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (kind == ScanKind.Custom)
        {
            var p = ValidateCustomPath(customPath);
            if (p is null) return Task.FromResult(new DefenderRunResult(false, null, TimeSpan.Zero, "Escolha um arquivo ou pasta existente (sem atalhos/links).", [], false));
            args.Add("-File");
            args.Add(p);
        }
        return RunAsync(args, update: false, progress, cancel);
    }

    public static Task<DefenderRunResult> UpdateSignaturesAsync(Action<string, TimeSpan> progress, CancellationToken cancel) =>
        RunAsync(["-SignatureUpdate"], update: true, progress, cancel);

    private static async Task<DefenderRunResult> RunAsync(IReadOnlyList<string> args, bool update, Action<string, TimeSpan> progress, CancellationToken cancel)
    {
        var exe = FindMpCmdRun();
        if (exe is null) return new DefenderRunResult(false, null, TimeSpan.Zero, "MpCmdRun.exe não encontrado: o Microsoft Defender não está instalado ou foi removido.", [], false);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var lines = new List<string>();
        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (lines) { if (lines.Count < 400) lines.Add(e.Data.Trim()); }
            progress(e.Data.Trim(), sw.Elapsed);
        }
        p.OutputDataReceived += OnLine;
        p.ErrorDataReceived += OnLine;
        if (!p.Start()) return new DefenderRunResult(false, null, TimeSpan.Zero, "Não foi possível iniciar o MpCmdRun.", [], false);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var cancelled = false;
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var wait = p.WaitForExitAsync(CancellationToken.None);
        while (!wait.IsCompleted)
        {
            if (cancel.IsCancellationRequested)
            {
                // Cancelar só encerra o MpCmdRun que o NEXUS iniciou; o Defender continua protegendo normalmente.
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                cancelled = true;
                break;
            }
            progress("", sw.Elapsed);
            await Task.WhenAny(wait, ticker.WaitForNextTickAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        }
        await wait.ConfigureAwait(false);
        sw.Stop();
        List<string> copy;
        lock (lines) copy = [.. lines];
        var summary = cancelled ? "Cancelado por você. Nada foi alterado." : DefenderOutput.Summarize(update, p.ExitCode, copy);
        return new DefenderRunResult(true, cancelled ? null : p.ExitCode, sw.Elapsed, summary, copy, cancelled);
    }
}
