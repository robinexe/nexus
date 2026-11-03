using System.Collections.ObjectModel;
using Nexus.Broker;
using Nexus.GameProfiles;
using Nexus.GameProfiles.Fortnite;
using Nexus.Lab.Gpu;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Placa de vídeo › Ajustes por jogo › Fortnite (avançado): todas as ações de catalog/fortnite.v1.json com escolha de valor.
/// Lê o GameUserSettings.ini do seu perfil (só leitura) e escreve só pelo aplicador, com o jogo fechado (o handler recusa com
/// ele aberto). O modo simples (Jogos) usa as mesmas ações em três opções de um clique.
/// </summary>
public sealed partial class GpuLabPage
{
    private bool _fnFound, _fnRunning;
    private string _fnStatus = Loc.T("Gpu.Fn.Reading"), _fnHint = "";
    private NvChoice? _fnMode, _fnFps, _fnReflex;

    public ObservableCollection<NvChoice> FnModes { get; } = [];
    public ObservableCollection<NvChoice> FnFpsOptions { get; } = [];
    public ObservableCollection<NvChoice> FnReflexOptions { get; } = [];
    public NvChoice? SelectedFnMode { get => _fnMode; set => Set(ref _fnMode, value); }
    public NvChoice? SelectedFnFps { get => _fnFps; set => Set(ref _fnFps, value); }
    public NvChoice? SelectedFnReflex { get => _fnReflex; set => Set(ref _fnReflex, value); }
    /// <summary>O GameUserSettings.ini existe neste perfil (o jogo já foi aberto).</summary>
    public bool FnFound { get => _fnFound; private set => Set(ref _fnFound, value); }
    public bool FnRunning { get => _fnRunning; private set => Set(ref _fnRunning, value); }
    /// <summary>Onde está e o que vale hoje (modo, limite, Reflex, V-Sync, desfoque, sombras).</summary>
    public string FnStatus { get => _fnStatus; private set => Set(ref _fnStatus, value); }
    /// <summary>Qual modo combina com esta placa (pela VRAM da GPU principal).</summary>
    public string FnHint { get => _fnHint; private set => Set(ref _fnHint, value); }

    public RelayCommand ApplyFnModeCommand { get; private set; } = null!;
    public RelayCommand ApplyFnFpsCommand { get; private set; } = null!;
    public RelayCommand ApplyFnReflexCommand { get; private set; } = null!;
    public RelayCommand ApplyFnVSyncCommand { get; private set; } = null!;
    public RelayCommand ApplyFnBlurCommand { get; private set; } = null!;
    public RelayCommand ApplyFnShadowsCommand { get; private set; } = null!;
    public RelayCommand ApplyFnGrassCommand { get; private set; } = null!;

    private void InitFortnite()
    {
        FnModes.Add(new NvChoice("es31", Loc.T("Gpu.Fn.ModePerf")));
        FnModes.Add(new NvChoice("sm6", Loc.T("Gpu.Fn.ModeDx12")));
        _fnMode = FnModes[0];
        foreach (var v in FortniteSettings.FpsLimits)
            FnFpsOptions.Add(new NvChoice(v, v == "0.000000" ? Loc.T("Gpu.Fn.NoLimit") : FortniteSettings.FpsLabel(v)));
        _fnFps = FnFpsOptions[0];
        FnReflexOptions.Add(new NvChoice(FortniteSettings.ReflexOn, Loc.T("Gpu.Fn.ReflexOn")));
        FnReflexOptions.Add(new NvChoice(FortniteSettings.ReflexOnBoost, Loc.T("Gpu.Fn.ReflexBoost")));
        FnReflexOptions.Add(new NvChoice("0", Loc.T("Gpu.Fn.ReflexOff")));
        _fnReflex = FnReflexOptions[0];

        bool Can() => !Busy && FnFound;
        ApplyFnModeCommand = new RelayCommand(() => _ = ApplyFnModeAsync(), () => Can() && SelectedFnMode is not null);
        ApplyFnFpsCommand = new RelayCommand(() => _ = ApplyFnFpsAsync(), () => Can() && SelectedFnFps is not null);
        ApplyFnReflexCommand = new RelayCommand(() => _ = ApplyFnReflexAsync(), () => Can() && SelectedFnReflex is not null);
        ApplyFnVSyncCommand = new RelayCommand(() => _ = ApplyFnFixedAsync(FortniteSettings.VSyncOffAction, Loc.T("Gpu.Fn.VSyncOff"), Loc.T("Gpu.Fn.VSyncOffText")), Can);
        ApplyFnBlurCommand = new RelayCommand(() => _ = ApplyFnFixedAsync(FortniteSettings.MotionBlurOffAction, Loc.T("Gpu.Fn.BlurOff"), Loc.T("Gpu.Fn.BlurOffText")), Can);
        ApplyFnShadowsCommand = new RelayCommand(() => _ = ApplyFnFixedAsync(FortniteSettings.ShadowsOffAction, Loc.T("Gpu.Fn.ShadowsOff"), Loc.T("Gpu.Fn.ShadowsOffText")), Can);
        ApplyFnGrassCommand = new RelayCommand(() => _ = ApplyFnFixedAsync(FortniteSettings.GrassOffAction, Loc.T("Gpu.Fn.GrassOff"), Loc.T("Gpu.Fn.GrassOffText")), Can);
    }

    /// <summary>Fortnite instalado (manifesto da Epic) → lê o GameUserSettings.ini do seu perfil e se o jogo está aberto. Só leitura.</summary>
    private async Task ReadFortniteAsync(GpuLabReport r)
    {
        var installed = r.Games.Any(g => string.Equals(System.IO.Path.GetFileName(g.ExePath), FortniteSettings.GameExe, StringComparison.OrdinalIgnoreCase));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var (state, running) = await Task.Run(() => (string.IsNullOrEmpty(local) ? FortniteState.NotFound : FortniteState.Read(local), FortniteProcesses.AnyRunning()));
        FnFound = state.FileFound;
        FnRunning = running;
        if (!state.FileFound)
        {
            FnStatus = installed ? Loc.T("Gpu.Fn.NoFile") : Loc.T("Gpu.Fn.NotInstalled");
            return;
        }
        string Show(string? v) => v ?? Loc.T("Gpu.Fn.Absent");
        FnStatus = Loc.F("Gpu.Fn.Status", Show(state.Rhi), Show(state.FeatureLevel), state.FrameRateLimit is { } f ? FortniteSettings.FpsLabel(f) : Loc.T("Gpu.Fn.Absent"),
                       Show(state.Reflex), Show(state.VSync), Show(state.MotionBlur), Show(state.Shadows), Show(state.Grass)) +
                   (running ? " " + Loc.T("Gpu.Fn.RunningNow") : "");
        if (FnModes.FirstOrDefault(m => string.Equals(m.Value, state.FeatureLevel, StringComparison.OrdinalIgnoreCase)) is { } mode) SelectedFnMode = mode;
        if (FnFpsOptions.FirstOrDefault(o => o.Value == state.FrameRateLimit) is { } fps && fps.Value != "0.000000") SelectedFnFps = fps;
        else
        {
            var monitors = r.Monitors;
            var monitor = monitors.FirstOrDefault(m => m.Primary) ?? (monitors.Count > 0 ? monitors[0] : null);
            if (monitor is not null && FortniteSettings.SuggestedFpsLimit(monitor.Current.Hz) is { } cap && FnFpsOptions.FirstOrDefault(o => o.Value == cap) is { } c) SelectedFnFps = c;
        }
        var gpu = r.Primary;
        var vramGb = gpu is null ? 0 : gpu.DedicatedVideoBytes / (1024.0 * 1024 * 1024);
        FnHint = gpu is null ? "" : vramGb >= 8 ? Loc.F("Gpu.Fn.HintStrong", gpu.Name, vramGb) : Loc.F("Gpu.Fn.HintWeak", gpu.Name, vramGb);
        if (gpu is { Vendor: not GpuVendor.Nvidia }) FnHint += " " + Loc.T("Gpu.Fn.NoReflex");
    }

    private Task ApplyFnModeAsync() => RunAsync(async () =>
    {
        if (Host is null || SelectedFnMode is not { } mode) return;
        var perf = mode.Value == "es31";
        var second = perf ? new ActionRequest(FortniteSettings.PerformanceModeAction, null) : new ActionRequest(FortniteSettings.Dx12FullAction, null);
        var text = (perf ? Loc.T("Gpu.Fn.ModePerfText") : Loc.T("Gpu.Fn.ModeDx12Text")) + " " + Loc.T("Gpu.Fn.CloseFirst");
        await ApplyAsync(Loc.T("Gpu.Fn.Title"), text,
            [new ActionRequest(FortniteSettings.RhiDx12Action, null), second], BrokerLevel.User, Loc.F("Gpu.Fn.Change", mode.Label));
    });

    private Task ApplyFnFpsAsync() => RunAsync(async () =>
    {
        if (Host is null || SelectedFnFps is not { } fps) return;
        await ApplyAsync(Loc.T("Gpu.Fn.Title"), Loc.F("Gpu.Fn.FpsText", fps.Label) + " " + Loc.T("Gpu.Fn.CloseFirst"),
            new ActionRequest(FortniteSettings.FpsLimitAction, fps.Value), BrokerLevel.User, Loc.F("Gpu.Fn.Change", Loc.F("Gpu.Fn.FpsChange", fps.Label)));
    });

    private Task ApplyFnReflexAsync() => RunAsync(async () =>
    {
        if (Host is null || SelectedFnReflex is not { } reflex) return;
        await ApplyAsync(Loc.T("Gpu.Fn.Title"), Loc.F("Gpu.Fn.ReflexText", reflex.Label) + " " + Loc.T("Gpu.Fn.CloseFirst"),
            new ActionRequest(FortniteSettings.ReflexAction, reflex.Value), BrokerLevel.User, Loc.F("Gpu.Fn.Change", reflex.Label));
    });

    private Task ApplyFnFixedAsync(string actionKey, string label, string text) => RunAsync(async () =>
    {
        if (Host is null) return;
        await ApplyAsync(Loc.T("Gpu.Fn.Title"), text + " " + Loc.T("Gpu.Fn.CloseFirst"), FixedRequest(actionKey), BrokerLevel.User, Loc.F("Gpu.Fn.Change", label));
    });

    /// <summary>Ações de valor fixo do Fortnite (V-Sync, desfoque, sombras, grama): só estas chaves.</summary>
    private static ActionRequest FixedRequest(string actionKey) => actionKey switch
    {
        FortniteSettings.VSyncOffAction => new ActionRequest(FortniteSettings.VSyncOffAction, null),
        FortniteSettings.MotionBlurOffAction => new ActionRequest(FortniteSettings.MotionBlurOffAction, null),
        FortniteSettings.ShadowsOffAction => new ActionRequest(FortniteSettings.ShadowsOffAction, null),
        FortniteSettings.GrassOffAction => new ActionRequest(FortniteSettings.GrassOffAction, null),
        _ => throw new ArgumentOutOfRangeException(nameof(actionKey)),
    };
}
