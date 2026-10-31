using System.Globalization;

namespace Nexus.Desktop.Views.Controls;

/// <summary>Tom semântico de um valor: decide a cor (verde = bom, âmbar = atenção, vermelho = problema, ciano = informação).</summary>
public enum Tone
{
    Neutral,
    Info,
    Good,
    Warn,
    Bad,
}

/// <summary>Como um número é escrito na tela.</summary>
public enum NumberKind
{
    /// <summary>Número comum em pt-BR (casas decimais conforme <c>Decimals</c>).</summary>
    Number,
    /// <summary>Bytes em base 1024 com a unidade (B, KB, MB, GB, TB), como o resto do NEXUS.</summary>
    Bytes,
    /// <summary>Porcentagem inteira ("87%").</summary>
    Percent,
}

/// <summary>
/// Lógica pura do design system (sem WPF): formatação de números, faixas de cor, frações de barra e anel, diferença
/// antes/depois. Os controles de <c>Views/Controls</c> só desenham o que sai daqui; os testes (DesignSystemTests) cobrem tudo.
/// </summary>
public static class DesignMath
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Escreve <paramref name="value"/> no formato pedido. <paramref name="decimals"/> &lt; 0 = automático.</summary>
    public static string Format(double value, NumberKind kind, int decimals = -1)
    {
        if (!double.IsFinite(value)) return "—";
        return kind switch
        {
            NumberKind.Bytes => Bytes(value),
            NumberKind.Percent => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Pt) + "%",
            _ => decimals switch
            {
                0 => Math.Round(value, MidpointRounding.AwayFromZero).ToString("N0", Pt),
                > 0 => value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), Pt),
                _ => Math.Abs(value - Math.Round(value)) < 0.05 ? Math.Round(value).ToString("N0", Pt) : value.ToString("N1", Pt),
            },
        };
    }

    /// <summary>Bytes em base 1024: "512 B", "3,4 GB", "120 GB" (sem casa decimal a partir de 100).</summary>
    public static string Bytes(double bytes)
    {
        if (!double.IsFinite(bytes)) return "—";
        var negative = bytes < 0;
        var v = Math.Abs(bytes);
        var u = 0;
        while (v >= 1024 && u < ByteUnits.Length - 1) { v /= 1024; u++; }
        var text = u == 0 ? Math.Round(v).ToString("0", Pt) : v.ToString(v >= 100 ? "0" : "0.0", Pt);
        return (negative ? "-" : "") + text + " " + ByteUnits[u];
    }

    /// <summary>
    /// Faixa de cor de um valor. <paramref name="higherIsBetter"/> = verdadeiro (nota, espaço livre): abaixo ou igual a
    /// <paramref name="badAt"/> é problema, abaixo ou igual a <paramref name="warnAt"/> é atenção. Falso (uso, temperatura):
    /// igual ou acima de <paramref name="badAt"/> é problema, igual ou acima de <paramref name="warnAt"/> é atenção.
    /// </summary>
    public static Tone ToneFor(double value, double warnAt, double badAt, bool higherIsBetter)
    {
        if (!double.IsFinite(value) || !double.IsFinite(warnAt) || !double.IsFinite(badAt)) return Tone.Neutral;
        if (higherIsBetter)
            return value <= badAt ? Tone.Bad : value <= warnAt ? Tone.Warn : Tone.Good;
        return value >= badAt ? Tone.Bad : value >= warnAt ? Tone.Warn : Tone.Good;
    }

    /// <summary>Chave do pincel do tema para o tom (Brush.Good, Brush.Trace, Brush.Bad, Brush.Signal, Brush.TextMuted).</summary>
    public static string BrushKey(Tone tone) => tone switch
    {
        Tone.Good => "Brush.Good",
        Tone.Warn => "Brush.Trace",
        Tone.Bad => "Brush.Bad",
        Tone.Info => "Brush.Signal",
        _ => "Brush.TextMuted",
    };

    /// <summary>Chave do fundo suave do tom (selos e chips).</summary>
    public static string SoftBrushKey(Tone tone) => tone switch
    {
        Tone.Good => "Brush.GoodSoft",
        Tone.Warn => "Brush.TraceSoft",
        Tone.Bad => "Brush.BadSoft",
        Tone.Info => "Brush.SignalSoft",
        _ => "Brush.Hover",
    };

    /// <summary>Ícone padrão do tom (Icon.Check, Icon.Warning, Icon.Error, Icon.Info).</summary>
    public static string IconKey(Tone tone) => tone switch
    {
        Tone.Good => "Icon.Check",
        Tone.Warn => "Icon.Warning",
        Tone.Bad => "Icon.Error",
        Tone.Info => "Icon.Info",
        _ => "Icon.Info",
    };

    /// <summary>Fração 0..1 de <paramref name="value"/> em <paramref name="maximum"/> (inválido ou máximo ≤ 0 = 0).</summary>
    public static double Fraction(double value, double maximum)
    {
        if (!double.IsFinite(value) || !double.IsFinite(maximum) || maximum <= 0) return 0;
        return Math.Clamp(value / maximum, 0, 1);
    }

    /// <summary>Barras do antes/depois: a maior ocupa a trilha inteira, a outra é proporcional.</summary>
    public static (double Before, double After) BarScales(double before, double after)
    {
        var b = double.IsFinite(before) ? Math.Max(0, before) : 0;
        var a = double.IsFinite(after) ? Math.Max(0, after) : 0;
        var max = Math.Max(a, b);
        return max <= 0 ? (0, 0) : (b / max, a / max);
    }

    /// <summary>Variação percentual de antes para depois; null quando antes é zero ou algum valor é inválido.</summary>
    public static double? PercentChange(double before, double after)
    {
        if (!double.IsFinite(before) || !double.IsFinite(after) || Math.Abs(before) < 1e-12) return null;
        return (after - before) / Math.Abs(before) * 100;
    }

    /// <summary>
    /// Texto e tom da diferença: "+12%" verde quando melhora, "-8%" vermelho quando piora, "igual" neutro.
    /// Abaixo de 0,5% conta como igual. Sem base (antes = 0), mostra a diferença absoluta no formato do valor.
    /// </summary>
    public static (string Text, Tone Tone) Delta(double before, double after, NumberKind kind, bool higherIsBetter)
    {
        if (!double.IsFinite(before) || !double.IsFinite(after)) return ("—", Tone.Neutral);
        var change = PercentChange(before, after);
        var diff = after - before;
        if (change is { } c && Math.Abs(c) < 0.5 || change is null && Math.Abs(diff) < 1e-9) return ("igual", Tone.Neutral);
        var better = higherIsBetter ? diff > 0 : diff < 0;
        var tone = better ? Tone.Good : Tone.Bad;
        if (change is { } p)
        {
            var rounded = Math.Round(p, MidpointRounding.AwayFromZero);
            if (rounded == 0) rounded = Math.Sign(p);
            return ((rounded > 0 ? "+" : "") + rounded.ToString("0", Pt) + "%", tone);
        }
        return ((diff > 0 ? "+" : "") + Format(diff, kind), tone);
    }

    /// <summary>
    /// "Itens em ordem": porcentagem das verificações do diagnóstico que não pedem atenção. Não é nota de desempenho;
    /// null quando nada foi avaliado ainda.
    /// </summary>
    public static int? HealthScore(int total, int attention)
    {
        if (total <= 0) return null;
        var ok = Math.Clamp(total - attention, 0, total);
        return (int)Math.Round(100.0 * ok / total, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Ponta do arco do anel: começa no topo (12 h) e anda no sentido horário <paramref name="fraction"/> da volta.
    /// Devolve o ponto final e se o arco passa de meia volta (IsLargeArc do ArcSegment).
    /// </summary>
    public static (double X, double Y, bool IsLargeArc) ArcEnd(double fraction, double radius, double centerX, double centerY)
    {
        var f = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        var angle = f * 2 * Math.PI;
        return (centerX + radius * Math.Sin(angle), centerY - radius * Math.Cos(angle), f > 0.5);
    }

    /// <summary>Curva de entrada (cúbica, desacelera no fim), para a contagem do número: 0 → 0, 1 → 1.</summary>
    public static double EaseOutCubic(double t)
    {
        var x = Math.Clamp(double.IsFinite(t) ? t : 1, 0, 1);
        return 1 - Math.Pow(1 - x, 3);
    }

    /// <summary>Valor intermediário da contagem de <paramref name="from"/> até <paramref name="to"/> no instante <paramref name="t"/>.</summary>
    public static double Tween(double from, double to, double t) =>
        !double.IsFinite(from) ? to : from + (to - from) * EaseOutCubic(t);
}
