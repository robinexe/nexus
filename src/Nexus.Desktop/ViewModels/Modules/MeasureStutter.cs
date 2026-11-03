using System.Globalization;
using Nexus.Measurement;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// "Por que travou?" em palavras simples (docs/modules/measure.md). A biblioteca dá as contagens por causa provável; aqui só se
/// escreve a frase no idioma da interface. Sempre "provavelmente": é inferência sobre contadores, não prova.
/// </summary>
public static class StutterWords
{
    private static CultureInfo Pt => Loc.Culture;

    /// <summary>Uma linha: "12 travadas: a maioria provavelmente por compilação de shader ou carregamento — normal nos primeiros minutos de um jogo novo".</summary>
    public static string Verdict(StutterReport? r)
    {
        if (r is null) return "";
        if (r.Events == 0) return Loc.T("Stutter.None");
        var count = r.Events == 1 ? Loc.T("Stutter.Count1") : Loc.F("Stutter.CountN", r.Events);
        var main = r.Main!.Value;
        if (r.Events == 1) return Loc.F("Stutter.Single", count, Cause(main, r)) + Hint(main);
        if (r.MainIsMajority) return Loc.F("Stutter.Majority", count, Cause(main, r)) + Hint(main);
        var top = Enum.GetValues<StutterCause>().Where(c => r.Count(c) > 0).OrderByDescending(r.Count).Take(2).Select(c => Cause(c, r));
        return Loc.F("Stutter.Mixed", count, string.Join(Loc.T("Stutter.And"), top));
    }

    public static Tone ToneOf(StutterReport? r) => r switch
    {
        null => Tone.Neutral,
        { Events: 0 } => Tone.Good,
        _ when r.PerMinute >= 2 => Tone.Bad,
        _ => Tone.Warn,
    };

    public static string Cause(StutterCause c, StutterReport? r = null) => c switch
    {
        StutterCause.Gpu => Loc.T("Stutter.Cause.Gpu"),
        StutterCause.Cpu => Loc.T("Stutter.Cause.Cpu"),
        StutterCause.ShaderOrStreaming => Loc.T("Stutter.Cause.Shader"),
        StutterCause.Background => r?.TopBackgroundProcess is { } p ? Loc.F("Stutter.Cause.BackgroundNamed", p) : Loc.T("Stutter.Cause.Background"),
        _ => Loc.T("Stutter.Cause.Unknown"),
    };

    private static string Hint(StutterCause c) => c switch
    {
        StutterCause.Gpu => Loc.T("Stutter.Hint.Gpu"),
        StutterCause.Cpu => Loc.T("Stutter.Hint.Cpu"),
        StutterCause.ShaderOrStreaming => Loc.T("Stutter.Hint.Shader"),
        StutterCause.Background => Loc.T("Stutter.Hint.Background"),
        _ => Loc.T("Stutter.Hint.Unknown"),
    };

    public static string Explain(StutterCause c) => c switch
    {
        StutterCause.Gpu => Loc.T("Stutter.Explain.Gpu"),
        StutterCause.Cpu => Loc.T("Stutter.Explain.Cpu"),
        StutterCause.ShaderOrStreaming => Loc.T("Stutter.Explain.Shader"),
        StutterCause.Background => Loc.T("Stutter.Explain.Background"),
        _ => Loc.T("Stutter.Explain.Unknown"),
    };

    /// <summary>Linhas por causa (só as que aconteceram), da mais comum para a menos.</summary>
    public static IReadOnlyList<StutterCauseRow> Rows(StutterReport? r) => r is null ? [] :
        Enum.GetValues<StutterCause>().Where(c => r.Count(c) > 0).OrderByDescending(r.Count)
            .Select(c => new StutterCauseRow(r.Count(c).ToString("0", Pt), Capitalize(Cause(c, r)), Explain(c))).ToList();

    /// <summary>"aos 2:13 · 87 ms (normal: 7 ms) · provável: placa de vídeo no limite".</summary>
    public static IReadOnlyList<string> Worst(StutterReport? r) => r is null ? [] :
        r.Worst.Select(e => Loc.F("Stutter.WorstLine", Clock(e.Seconds), e.FrametimeMs, e.MedianMs, Cause(e.Cause, r))).ToList();

    /// <summary>O que faltou nesta partida e o que o modo administrador acrescentaria.</summary>
    public static string Notes(StutterReport? r)
    {
        if (r is null) return "";
        var parts = new List<string>();
        if (!r.HasCpuGpuData) parts.Add(Loc.T("Stutter.Note.NoCpuGpu"));
        if (!r.HasActivityData) parts.Add(Loc.T("Stutter.Note.NoActivity"));
        else if (!r.HasDiskData) parts.Add(Loc.T("Stutter.Note.NoDisk"));
        if (r.GeneratedFrames > 0) parts.Add(Loc.F("Stutter.Note.Generated", r.GeneratedFrames));
        parts.Add(Loc.T("Stutter.Note.Method"));
        parts.Add(Loc.T("Stutter.Note.Admin"));
        return string.Join("\n\n", parts);
    }

    private static string Clock(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", Pt);

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Pt) + s[1..];
}

/// <summary>Uma causa na tela: quantas, nome e o que quer dizer.</summary>
public sealed record StutterCauseRow(string Count, string Title, string Explanation);
