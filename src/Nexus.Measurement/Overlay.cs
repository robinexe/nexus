using System.Globalization;

namespace Nexus.Measurement;

/// <summary>O que a janela do mostrador sabe fazer (a real é uma janela WPF transparente; nos testes, um falso).</summary>
public interface IOverlaySurface
{
    void Show(OverlayCorner corner);
    void Hide();
    void Render(OverlayContent content);
}

/// <summary>Leituras do sistema para o mostrador (opcionais: null = não mostrar).</summary>
public readonly record struct SystemLoad(double? CpuPercent, double? GpuPercent, double? GpuTemperatureC);

/// <summary>Linhas prontas do mostrador.</summary>
public sealed record OverlayContent(string Fps, string Low1, string Frametime, string? System, bool Waiting);

/// <summary>
/// Estado do mostrador de FPS, sem janela (testável): ligado/desligado pelo botão ou pelo atalho, ligar sozinho ao abrir
/// um jogo (preferência), canto da tela. Só fica VISÍVEL quando ligado e há um jogo aberto (ou na prévia pedida na página).
/// O mostrador é uma janela própria do NEXUS por cima das outras: não entra no processo do jogo.
/// </summary>
public sealed class OverlayController(IOverlaySurface surface)
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private readonly IOverlaySurface _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    private bool _shown;

    public bool Enabled { get; private set; }
    public bool GameRunning { get; private set; }
    public bool Preview { get; private set; }
    public bool ShowSystem { get; set; } = true;
    public OverlayCorner Corner { get; private set; } = OverlayCorner.TopLeft;
    public bool Visible => _shown;
    public event Action? Changed;

    public void SetEnabled(bool on) { Enabled = on; Sync(); }

    /// <summary>O atalho global: alterna ligado/desligado.</summary>
    public void Toggle() => SetEnabled(!Enabled);

    public void SetPreview(bool on) { Preview = on; Sync(); }

    public void SetCorner(OverlayCorner corner)
    {
        if (Corner == corner) return;
        Corner = corner;
        if (_shown) _surface.Show(Corner);
        Changed?.Invoke();
    }

    /// <summary>Jogo abriu/fechou. <paramref name="autoEnable"/> = preferência "ligar o mostrador ao abrir um jogo".</summary>
    public void OnGame(bool running, bool autoEnable)
    {
        GameRunning = running;
        if (running && autoEnable) Enabled = true;
        Sync();
    }

    public void Update(LiveReading reading, SystemLoad load)
    {
        if (!_shown) return;
        _surface.Render(Format(reading, ShowSystem ? load : default));
    }

    private void Sync()
    {
        var want = Enabled && (GameRunning || Preview);
        if (want && !_shown) { _surface.Show(Corner); _shown = true; _surface.Render(Format(LiveReading.Empty, default)); }
        else if (!want && _shown) { _surface.Hide(); _shown = false; }
        Changed?.Invoke();
    }

    public static OverlayContent Format(LiveReading r, SystemLoad load)
    {
        string N(double v, string fmt) => double.IsFinite(v) ? v.ToString(fmt, Pt) : "—";
        var parts = new List<string>();
        if (load.CpuPercent is { } c) parts.Add($"CPU {c.ToString("0", Pt)}%");
        if (load.GpuPercent is { } g) parts.Add($"GPU {g.ToString("0", Pt)}%");
        if (load.GpuTemperatureC is { } t) parts.Add($"{t.ToString("0", Pt)} °C");
        return new OverlayContent(
            N(r.CurrentFps, "0") + " FPS",
            "1% low " + N(r.Low1Fps, "0"),
            N(r.FrametimeMs, "0.0") + " ms",
            parts.Count > 0 ? string.Join(" · ", parts) : null,
            !r.HasData);
    }

    /// <summary>Posição (x, y) do canto superior esquerdo do mostrador dentro da área de trabalho, com margem.</summary>
    public static (double X, double Y) Position(OverlayCorner corner, double areaLeft, double areaTop, double areaWidth, double areaHeight,
        double width, double height, double margin = 16) => corner switch
    {
        OverlayCorner.TopRight => (areaLeft + areaWidth - width - margin, areaTop + margin),
        OverlayCorner.BottomLeft => (areaLeft + margin, areaTop + areaHeight - height - margin),
        OverlayCorner.BottomRight => (areaLeft + areaWidth - width - margin, areaTop + areaHeight - height - margin),
        _ => (areaLeft + margin, areaTop + margin),
    };
}
