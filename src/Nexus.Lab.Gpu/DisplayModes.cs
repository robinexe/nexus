using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Lab.Gpu;

/// <summary>Modo de vídeo (resolução + taxa inteira em Hz, como o Windows lista em EnumDisplaySettingsEx).</summary>
public sealed partial record DisplayMode(int Width, int Height, int Hz, int BitsPerPixel = 32, bool Interlaced = false)
{
    /// <summary>Formato canônico usado no catálogo e no diário: "2560x1440@144".</summary>
    public string Key => $"{Width.ToString(CultureInfo.InvariantCulture)}x{Height.ToString(CultureInfo.InvariantCulture)}@{Hz.ToString(CultureInfo.InvariantCulture)}";

    public string Label => $"{Width}×{Height} a {Hz} Hz";

    public bool SameResolution(DisplayMode o) => Width == o.Width && Height == o.Height;

    [GeneratedRegex(@"\A([0-9]{3,5})x([0-9]{3,5})@([0-9]{2,3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    /// <summary>Lê "2560x1440@144". Recusa valores fora de 640×480..16384×16384 e 23..500 Hz.</summary>
    public static DisplayMode? TryParse(string? key)
    {
        if (key is null) return null;
        var m = KeyPattern().Match(key);
        if (!m.Success) return null;
        int w = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            hz = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (w < 640 || h < 480 || w > 16384 || h > 16384 || hz < 23 || hz > 500) return null;
        return new DisplayMode(w, h, hz);
    }
}

/// <summary>Análise de um monitor: modo atual contra o melhor que ele lista na MESMA resolução.</summary>
/// <param name="Best">Maior taxa listada na resolução atual (mesma profundidade de cor, sem entrelaçado).</param>
/// <param name="Native">Maior resolução listada (normalmente a nativa do painel).</param>
public sealed record DisplayModeAdvice(DisplayMode Current, DisplayMode? Best, DisplayMode? Native)
{
    /// <summary>Há uma taxa maior disponível na mesma resolução (o caso "monitor de 144 Hz rodando a 60 Hz").</summary>
    public bool BelowMaxRefresh => Best is { } b && b.Hz > Current.Hz;

    public bool BelowNativeResolution => Native is { } n && (long)n.Width * n.Height > (long)Current.Width * Current.Height;
}

public static class DisplayModes
{
    /// <summary>
    /// Escolhe o melhor modo SEGURO: só muda a taxa, nunca a resolução (mudar resolução muda escala, posição de janelas e pode
    /// cair em modo de DSR/VSR que não é nativo). Considera só modos com a mesma profundidade de cor e progressivos.
    /// </summary>
    public static DisplayModeAdvice Advise(DisplayMode current, IEnumerable<DisplayMode> listed)
    {
        ArgumentNullException.ThrowIfNull(current);
        var valid = listed.Where(m => !m.Interlaced && m.BitsPerPixel == current.BitsPerPixel).ToList();
        var best = valid.Where(m => m.SameResolution(current)).OrderByDescending(m => m.Hz).FirstOrDefault();
        var native = valid.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Hz).FirstOrDefault();
        return new DisplayModeAdvice(current, best, native);
    }

    /// <summary>O modo pedido está na lista do monitor (mesma resolução e taxa, progressivo)?</summary>
    public static bool IsListed(DisplayMode wanted, IEnumerable<DisplayMode> listed) =>
        listed.Any(m => !m.Interlaced && m.Width == wanted.Width && m.Height == wanted.Height && m.Hz == wanted.Hz);

    /// <summary>Nome GDI do monitor ("\\.\DISPLAY1"). Mesmo padrão do catálogo (userParameters).</summary>
    public const string DevicePattern = @"\\\\\.\\DISPLAY[0-9]{1,2}";

    public static bool IsValidDevice(string? device) =>
        device is not null && Regex.IsMatch(device, @"\A" + DevicePattern + @"\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
