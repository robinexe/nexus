using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Nexus.Desktop.Services.Shell;
using Nexus.Updates;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Atualização do programa (docs/11-atualizacao.md). Com "Atualizar automaticamente" ligado: procura 30 s depois de abrir e a
/// cada 6 h; achando versão nova assinada, baixa em segundo plano e confere (assinatura, host, tamanho, SHA-256); então mostra
/// a faixa "pronto — Reiniciar e atualizar" na janela. Ignorada, instala ao fechar o NEXUS (uma tentativa por versão).
/// Portátil e versão antiga demais: só avisa com link. Versão de desenvolvimento e autoteste: desligado.
/// Instalar = o instalador Inno conferido, em silêncio; o Windows pede permissão uma vez; o instalador reabre o NEXUS.
/// </summary>
public sealed class AppUpdates : ObservableObject
{
    public static AppUpdates Instance { get; } = new();

    public static TimeSpan FirstCheckDelay { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan CheckInterval { get; } = TimeSpan.FromHours(6);

    private readonly AppVersionInfo _version = AppVersionInfo.FromAssembly(typeof(AppUpdates).Assembly);
    private InstallKind? _kind;
    private DispatcherTimer? _timer;
    private bool _started;
    private bool _disabledBySelfTest;
    private bool _working;
    private bool _installStarted;
    private string _status = "";
    private double _progress;
    private bool _showProgress;
    private UpdateManifest? _ready;
    private string? _readyPath;
    private UpdateManifest? _notify;
    private string _bannerText = "";
    private string _bannerNote = "";
    private BannerKind _banner;

    private enum BannerKind { None, Ready, Notify, Info }

    private AppUpdates()
    {
        CheckNowCommand = new RelayCommand(() => _ = CheckAsync(), () => CanCheck);
        InstallNowCommand = new RelayCommand(InstallNow, () => _ready is not null && !_installStarted);
        LaterCommand = new RelayCommand(Later);
        OpenDownloadsCommand = new RelayCommand(OpenDownloads);
        _status = Disabled ? Loc.T("Shell.VersaoDesenvolvimentoAtualizacaoAuto") : "";
    }

    /// <summary>Operação em andamento (aplicar, Assistente, leitura): não instala no meio. Padrão: olha a janela principal.</summary>
    public static Func<bool> IsAppBusy { get; set; } = DefaultIsAppBusy;

    public RelayCommand CheckNowCommand { get; }
    public RelayCommand InstallNowCommand { get; }
    public RelayCommand LaterCommand { get; }
    public RelayCommand OpenDownloadsCommand { get; }

    public InstallKind Kind => _kind ??= OperatingSystem.IsWindows() ? UpdateInstallation.Detect(_version.Packaged, AppContext.BaseDirectory) : InstallKind.Development;
    private bool Disabled => Kind == InstallKind.Development || _disabledBySelfTest;

    public string CurrentVersion => _version.Display;
    public string CurrentVersionText => Kind switch
    {
        InstallKind.Development => Loc.F("Shell.NexusNVersaoDesenvolvimento", _version.Display),
        InstallKind.Portable => Loc.F("Shell.NexusNPortatil", _version.Display),
        _ => Loc.F("Shell.NexusNInstalado", _version.Display),
    };

    public string KindNote => Kind switch
    {
        InstallKind.Development => Loc.T("Shell.CompiladaPcNaoProcura"),
        InstallKind.Portable => Loc.T("Shell.CopiaPortatilAvisaQuando"),
        _ => Loc.T("Shell.InstalaSozinhoWindowsPede"),
    };

    public bool CanCheck => !Disabled && !_working && !_installStarted;

    public bool AutoUpdate
    {
        get => UiSettings.Current.AutoUpdate;
        set
        {
            if (UiSettings.Current.AutoUpdate == value) return;
            UiSettings.Update(s => s with { AutoUpdate = value });
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoTone));
            OnPropertyChanged(nameof(AutoChip));
            if (_started) Schedule(value ? FirstCheckDelay : null);
        }
    }

    public Views.Controls.Tone AutoTone => Disabled ? Views.Controls.Tone.Neutral : AutoUpdate ? Views.Controls.Tone.Good : Views.Controls.Tone.Warn;
    public string AutoChip => Disabled ? Loc.T("Shell.Desligado") : AutoUpdate ? Loc.T("Nav.Automatico") : Loc.T("Shell.Manual");
    public bool IsEnabled => !Disabled;

    public string LastCheckText => UiSettings.Current.LastUpdateCheck is { } t
        ? Loc.T("Shell.UltimaProcura") + t.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"))
        : Disabled ? "" : Loc.T("Shell.AindaNaoProcurou");

    public string Status { get => _status; private set => Set(ref _status, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool ShowProgress { get => _showProgress; private set => Set(ref _showProgress, value); }
    public bool Working
    {
        get => _working;
        private set { if (Set(ref _working, value)) OnPropertyChanged(nameof(CanCheck)); }
    }

    // ---- faixa na janela principal
    public bool BannerVisible => _banner != BannerKind.None;
    public bool BannerIsReady => _banner == BannerKind.Ready;
    public bool BannerIsNotify => _banner == BannerKind.Notify;
    public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }
    public string BannerNote { get => _bannerNote; private set => Set(ref _bannerNote, value); }
    public string DismissText => _banner == BannerKind.Info ? Loc.T("Shell.Fechar") : Loc.T("Shell.Depois");
    public bool HasReady => _ready is not null;
    public bool HasNotify => _notify is not null;
    public string ReadyNotes => _ready?.Notes ?? _notify?.Notes ?? "";

    private void ShowBanner(BannerKind kind, string text, string note)
    {
        _banner = kind;
        BannerText = text;
        BannerNote = note;
        OnPropertyChanged(nameof(BannerVisible));
        OnPropertyChanged(nameof(BannerIsReady));
        OnPropertyChanged(nameof(BannerIsNotify));
        OnPropertyChanged(nameof(DismissText));
    }

    // ------------------------------------------------------------------ ciclo de vida

    /// <summary>Chamado pela App depois de abrir a janela. Argumentos --updated / --update-failed vêm do instalador.</summary>
    public void Start(IReadOnlyList<string> args, bool selfTest)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (_started) return;
        _started = true;
        _disabledBySelfTest = selfTest;
        if (selfTest) { Status = Loc.T("Shell.AutotesteAtualizacaoDesligada"); RaiseState(); return; }
        if (args.Contains("--updated")) ShowBanner(BannerKind.Info, Loc.F("Shell.NexusAtualizadoN", _version.Display), "");
        else if (args.Contains("--update-failed"))
            ShowBanner(BannerKind.Info, Loc.T("Shell.AtualizacaoNaoFoiConcluida"), Loc.T("Shell.AvisoWindowsFoiRecusado"));
        if (Disabled) return;
        if (OperatingSystem.IsWindows()) UpdateClient.CleanOld(DownloadRoot, null); // instalada a versão nova, os downloads antigos sobram
        Schedule(AutoUpdate ? FirstCheckDelay : null);
    }

    private void Schedule(TimeSpan? after)
    {
        _timer?.Stop();
        if (after is null) return;
        _timer ??= new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Interval = after.Value;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer!.Interval = CheckInterval;
        if (AutoUpdate) _ = CheckAsync();
    }

    /// <summary>%LocalAppData%\NEXUS\updates.</summary>
    public static string DownloadRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "updates");

    // ------------------------------------------------------------------ procurar e baixar

    public async Task CheckAsync()
    {
        if (!CanCheck) return;
        Working = true;
        try
        {
            Status = Loc.T("Shell.ProcurandoVersaoNova");
            using var client = new UpdateClient(_version.Version);
            var r = await client.CheckAsync();
            if (r.Status != UpdateCheckStatus.Failed) UiSettings.Update(s => s with { LastUpdateCheck = DateTimeOffset.Now });
            OnPropertyChanged(nameof(LastCheckText));
            switch (r.Status)
            {
                case UpdateCheckStatus.UpToDate: Status = Loc.T("Shell.VoceEstaVersaoMais"); break;
                case UpdateCheckStatus.NotPublished: Status = Loc.T("Shell.NenhumaVersaoPublicadaAinda"); break;
                case UpdateCheckStatus.Failed: Status = Loc.F("Shell.NaoDeuProcurarN", r.Message); break;
                case UpdateCheckStatus.Available when r.Manifest is { } m:
                    if (_ready?.Version == m.Version) { Status = Loc.F("Shell.NexusNJaEsta", m.Version); break; }
                    if (UpdateInstallation.ModeFor(Kind, m, _version.Version) == UpdateMode.Install) await DownloadAsync(client, m);
                    else Notify(m);
                    break;
            }
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
        {
            Status = Loc.F("Shell.AtualizacaoInterrompidaN", ex.Message);
        }
        finally
        {
            Working = false;
            ShowProgress = false;
            RaiseState();
        }
    }

    private void Notify(UpdateManifest m)
    {
        _notify = m;
        var why = Kind == InstallKind.Portable ? Loc.T("Shell.CopiaPortatilBaixePacote") : Loc.T("Shell.EstaVersaoAntigaDemais");
        Status = Loc.F("Shell.NexusNDisponivelN", m.Version, why);
        ShowBanner(BannerKind.Notify, Loc.F("Shell.NexusNDisponivel", m.Version), why);
    }

    private async Task DownloadAsync(UpdateClient client, UpdateManifest m)
    {
        Status = Loc.F("Shell.BaixandoNexusN", m.Version);
        Progress = 0;
        ShowProgress = true;
        var progress = new System.Progress<DownloadProgress>(p =>
        {
            Progress = p.Fraction * 100;
            Status = Loc.F("Shell.BaixandoNexusNN", m.Version, p.Fraction * 100, p.Total / 1048576.0);
        });
        var path = await client.DownloadAsync(m, DownloadRoot, progress);
        UpdateClient.CleanOld(DownloadRoot, m.Version);
        _ready = m;
        _readyPath = path;
        Status = Loc.F("Shell.NexusNBaixadoConferido", m.Version);
        ShowBanner(BannerKind.Ready, Loc.F("Shell.NexusNPronto", m.Version), Loc.T("Shell.WindowsPedePermissaoVez"));
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(HasReady));
        OnPropertyChanged(nameof(HasNotify));
        OnPropertyChanged(nameof(ReadyNotes));
        OnPropertyChanged(nameof(AutoTone));
        OnPropertyChanged(nameof(AutoChip));
        OnPropertyChanged(nameof(IsEnabled));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ instalar

    private void InstallNow()
    {
        if (_ready is null || _readyPath is null || _installStarted) return;
        if (IsAppBusy()) { Status = Loc.T("Shell.TermineOperacaoAndamentoAplicar"); BannerNote = Status; return; }
        if (!StartInstaller(relaunch: true)) return;
        System.Windows.Application.Current?.Shutdown();
    }

    private void Later()
    {
        if (_banner == BannerKind.Ready) Status = Loc.F("Shell.NexusNInstalaQuando", _ready?.Version);
        ShowBanner(BannerKind.None, "", "");
    }

    /// <summary>Chamado pela App ao sair: instala a versão pronta (uma tentativa por versão; nunca no meio de uma operação).</summary>
    public void OnAppExit()
    {
        if (_ready is null || _installStarted || Disabled || !AutoUpdate || Kind != InstallKind.Installed) return;
        var version = _ready.Version.ToString();
        if (UiSettings.Current.UpdateExitAttempted == version || IsAppBusy()) return;
        UiSettings.Update(s => s with { UpdateExitAttempted = version });
        StartInstaller(relaunch: false);
    }

    private bool StartInstaller(bool relaunch)
    {
        if (!OperatingSystem.IsWindows() || _ready is null || _readyPath is null) return false;
        try
        {
            var log = Path.Combine(Path.GetDirectoryName(_readyPath)!, "instalacao.log");
            UpdateInstallation.Start(_readyPath, _ready.Installer, relaunch, Environment.ProcessPath, log);
            _installStarted = true;
            RaiseState();
            return true;
        }
        catch (Exception ex) when (ex is UpdateException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Status = Loc.F("Shell.NaoFoiPossivelIniciar", ex.Message);
            if (ex is UpdateException) { _ready = null; _readyPath = null; ShowBanner(BannerKind.None, "", ""); RaiseState(); }
            return false;
        }
    }

    private void OpenDownloads()
    {
        try
        {
            // Página pública de downloads no navegador (o usuário vê e decide).
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdatePolicy.ReleasesPage.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Status = Loc.F("Asst.NaoFoiPossivelAbrir", ex.Message); }
    }

    private static bool DefaultIsAppBusy()
    {
        if (System.Windows.Application.Current?.MainWindow?.DataContext is not MainViewModel vm) return false;
        return vm.Busy || vm.Plan.Busy || vm.Broker.Busy || vm.Pages.OfType<ModulePage>().Any(p => p.Busy && p is not SettingsPage);
    }
}
