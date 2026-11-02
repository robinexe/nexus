using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Nexus.Broker;
using Nexus.DriverUpdates;
using Nexus.DriverUpdates.Vendor;
using Nexus.DriverUpdates.Windows;
using Nexus.Lab.Gpu;
using UiTone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>Uma atualização de driver oferecida pelo Windows Update, com seleção, estado e Desfazer.</summary>
public sealed class DriverUpdateRow : ObservableObject
{
    private string _status = "";
    private string _tone = "muted";
    private Guid? _runId;
    private bool _applied;
    private bool _isChecked;

    public required DriverUpdateOffer Offer { get; init; }
    public string UpdateId => Offer.Update.UpdateId;
    public string Revision => Offer.Update.Revision.ToString(CultureInfo.InvariantCulture);
    public string Title => Offer.Device;
    public string UpdateTitle => Offer.Update.Title;
    public string VersionLine => Offer.VersionLine;
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";
    public string RollbackText => Offer.Rollback.Possible ? "Desfazer: " + Offer.Rollback.Explanation : "Sem desfazer automático: " + Offer.Rollback.Explanation;
    public bool RollbackPossible => Offer.Rollback.Possible;
    public string? Blocked => Offer.Blocked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public bool CanApply => Blocked is null && !_applied;
    public string Status { get => _status; set => Set(ref _status, value); }
    /// <summary>good, bad, trace, signal ou muted (cor do selo).</summary>
    public string Tone { get => _tone; set { if (Set(ref _tone, value)) OnPropertyChanged(nameof(ToneValue)); } }
    /// <summary><see cref="Tone"/> no design system (cor do estado e do selo).</summary>
    public UiTone ToneValue => DriverUpdatesPage.UiToneOf(_tone);
    /// <summary>Selo do tipo: verde recomendada, âmbar opcional, neutro bloqueada.</summary>
    public UiTone BadgeTone => Blocked is not null ? UiTone.Neutral : Offer.Update.Optional ? UiTone.Warn : UiTone.Good;
    public UiTone RollbackTone => RollbackPossible ? UiTone.Good : UiTone.Warn;
    public string RollbackChip => RollbackPossible ? "tem desfazer" : "sem desfazer automático";
    public Guid? RunId { get => _runId; set { if (Set(ref _runId, value)) OnPropertyChanged(nameof(CanRevert)); } }
    public bool Applied
    {
        get => _applied;
        set
        {
            if (!Set(ref _applied, value)) return;
            if (value) IsChecked = false;
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanRevert));
        }
    }
    public bool CanRevert => _applied && _runId is not null;
    public ICommand? RevertCommand { get; set; }
    public ICommand? DetailsCommand { get; set; }
    /// <summary>Instalar só esta (com o progresso do aplicador).</summary>
    public ICommand? InstallCommand { get; set; }

    /// <summary>O alvo do diário ("revision=N | updateId=…") é desta linha.</summary>
    public bool Matches(string? target) => target is not null && target.Split(" | ").Contains("updateId=" + UpdateId, StringComparer.Ordinal);
}

/// <summary>
/// Placa de vídeo: versão e idade do driver; "Procurar versão nova" no serviço oficial do fabricante e "Baixar e instalar" no próprio
/// NEXUS (download com progresso, conferência da assinatura e instalação pelo aplicador). Página oficial e app ficam como links.
/// </summary>
public sealed class GpuDriverCard : ObservableObject
{
    private GpuUpdateCheck? _check;
    private bool _checking, _downloading, _working, _installed;
    private double _progress;
    private string _progressText = "", _phase = "", _error = "", _result = "";
    private Guid? _runId;

    public required GpuDriverStatus Status { get; init; }
    public string Name => Status.Name;
    public string Kind => (Status.Integrated ? "integrada" : "dedicada") + " · " + GpuIdentity.VendorName(Status.Vendor);
    public string Version => Status.VendorVersion is { } v ? $"{v}  ({Status.WindowsVersion})"
        : Status.PackageVersion is { } p ? $"Adrenalin {p}  ({Status.WindowsVersion})"
        : Status.WindowsVersion.Length > 0 ? Status.WindowsVersion : "n/d";
    public string Age => Status.Date is { } d
        ? $"de {d.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)} · {(Status.AgeDays ?? 0).ToString(CultureInfo.CurrentCulture)} dias"
        : "data não informada";
    public string Verdict => Status.Verdict;
    /// <summary>good, info, warn, bad.</summary>
    public string Level => Status.Level;
    /// <summary>Idade do driver em dias (NaN se o Windows não informa a data): o número do cartão.</summary>
    public double AgeNumber => Status.AgeDays is { } d ? d : double.NaN;
    public UiTone LevelTone => Status.Level switch { "good" => UiTone.Good, "warn" => UiTone.Warn, "bad" => UiTone.Bad, _ => UiTone.Info };
    public string KindChip => Status.Integrated ? "integrada" : "dedicada";
    public string VendorName => GpuIdentity.VendorName(Status.Vendor);
    public string DateText => Status.Date is { } d ? "de " + d.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) : "data não informada";
    public bool HasPage => Status.OfficialPage is not null;
    public string PageLabel => $"Página oficial da {GpuIdentity.VendorName(Status.Vendor)}";
    public bool HasApp => Status.App is not null;
    public string AppLabel => Status.App is { } a ? $"Abrir {a.Name}" : "";
    /// <summary>NVIDIA e AMD: o NEXUS procura, baixa e instala. Intel: só o link oficial.</summary>
    public bool CanUpdateInApp => Status.Vendor is GpuVendor.Nvidia or GpuVendor.Amd;

    // ------------------------------------------------------------------ procurar / baixar / instalar

    public GpuUpdateCheck? Check
    {
        get => _check;
        set
        {
            if (!Set(ref _check, value)) return;
            foreach (var n in new[] { nameof(CheckMessage), nameof(CheckTone), nameof(HasUpdate), nameof(InstallLabel), nameof(Note), nameof(HasNote),
                         nameof(IsUpToDate), nameof(HasAutoDetect), nameof(CanCheck), nameof(CanInstall), nameof(ReleaseLine) })
                OnPropertyChanged(n);
        }
    }

    public bool Checking { get => _checking; set { if (Set(ref _checking, value)) { OnPropertyChanged(nameof(CanCheck)); OnPropertyChanged(nameof(CheckMessage)); } } }
    public bool Downloading { get => _downloading; set => Set(ref _downloading, value); }
    /// <summary>Baixando, conferindo ou instalando (botões travados).</summary>
    public bool Working { get => _working; set { if (Set(ref _working, value)) { OnPropertyChanged(nameof(CanCheck)); OnPropertyChanged(nameof(CanInstall)); } } }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    /// <summary>"Conferindo assinatura…", "Instalando pelo aplicador…".</summary>
    public string Phase { get => _phase; set => Set(ref _phase, value); }
    public string Error { get => _error; set => Set(ref _error, value); }
    public string Result { get => _result; set => Set(ref _result, value); }
    public Guid? RunId { get => _runId; set { if (Set(ref _runId, value)) OnPropertyChanged(nameof(CanRevert)); } }
    public bool Installed { get => _installed; set { if (Set(ref _installed, value)) { OnPropertyChanged(nameof(CanRevert)); OnPropertyChanged(nameof(CanInstall)); } } }
    public bool CanRevert => _installed && _runId is not null && !_working;

    public string CheckMessage => _checking ? "Procurando a versão mais nova no site do fabricante…" : _check?.Message ?? "";
    public UiTone CheckTone => _check?.State switch
    {
        GpuUpdateState.UpToDate => UiTone.Good,
        GpuUpdateState.Available => UiTone.Warn,
        GpuUpdateState.Failed => UiTone.Bad,
        _ => UiTone.Info,
    };
    public bool IsUpToDate => _check?.State == GpuUpdateState.UpToDate;
    public bool HasUpdate => _check?.State == GpuUpdateState.Available;
    public bool HasAutoDetect => _check?.State == GpuUpdateState.AutoDetectOnly && _check.AutoDetectUrl is not null;
    public bool CanCheck => CanUpdateInApp && !_checking && !_working;
    public bool CanInstall => HasUpdate && !_working && !_installed;
    public string InstallLabel => _check is { Offer: { } o, ExactBytes: > 0 } c ? $"Baixar e instalar {o.Version} ({DriverUpdatesReader.FormatSize(c.ExactBytes)})" : "Baixar e instalar";
    public string ReleaseLine => _check?.Offer is { } o
        ? $"{o.Label}{(o.ReleaseDate is { } d ? " · de " + d.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) : "")} · servidor oficial {o.DownloadUrl.Host}"
        : "";
    public string? Note => _check?.Note;
    public bool HasNote => !string.IsNullOrEmpty(_check?.Note);

    public ICommand? OpenPageCommand { get; set; }
    public ICommand? OpenAppCommand { get; set; }
    public ICommand? CheckCommand { get; set; }
    public ICommand? InstallCommand { get; set; }
    public ICommand? CancelCommand { get; set; }
    public ICommand? AutoDetectCommand { get; set; }
    public ICommand? RevertCommand { get; set; }
}

/// <summary>
/// Atualizar drivers: procura no Windows Update (leitura, direto da biblioteca, com tempo limite e cancelamento), mostra o que
/// cada oferta troca e instala as escolhidas SÓ pelo aplicador (Host.ApplyAsync, administrador, ponto de restauração automático).
/// Placa de vídeo: versão e idade do driver; NVIDIA/AMD: procura, baixa e instala o driver do fabricante (instalação só pelo aplicador).
/// </summary>
public sealed class DriverUpdatesPage : ModulePage
{
    public override string Title => "Atualizar drivers";
    public override string Group => Loc.T("Nav.Sistema");
    public override string Subtitle => "Drivers oficiais pelo Windows Update e o driver de vídeo direto da NVIDIA e da AMD.";

    private const string InstallAction = "driverupdates.install@1";
    private const string GpuInstallAction = "driverupdates.gpu.install@1";
    public const int MaxBatch = BrokerService.MaxActionsPerRequest;

    private bool _loaded, _searching, _searched;
    private string _searchStatus = "", _summary = "Clique em Procurar atualizações. A busca usa o próprio Windows Update e pode levar alguns minutos.";
    private string _error = "", _reboot = "", _gpuNote = "";
    private Action? _cancel;
    private readonly string _whenGpu = GpuDrivers.WhenToUpdate;
    private (Guid Run, string Label)? _lastBatch;
    private GpuDriverUpdater? _updater;
    private CancellationTokenSource? _gpuCancel;
    private bool _gpuChecked;

    public DriverUpdatesPage()
    {
        SearchCommand = new RelayCommand(() => _ = RunAsync(SearchAsync), () => !Busy);
        CancelCommand = new RelayCommand(() => _cancel?.Invoke(), () => _searching);
        InstallSelectedCommand = new RelayCommand(() => _ = RunAsync(() => InstallAsync(Selected())), () => !Busy && Selected().Count > 0);
        UndoCommand = new RelayCommand(() => _ = RunAsync(UndoBatchAsync), () => !Busy && _lastBatch is not null);
        OpenWindowsUpdateCommand = new RelayCommand(OpenWindowsUpdate);
        SelectRecommendedCommand = new RelayCommand(() => { foreach (var r in Recommended.Where(r => r.CanApply)) r.IsChecked = true; });
        InstallRecommendedCommand = new RelayCommand(() => _ = RunAsync(() => InstallAsync(Recommended.Where(r => r.CanApply).ToList())),
            () => !Busy && Recommended.Any(r => r.CanApply));
    }

    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand InstallSelectedCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand OpenWindowsUpdateCommand { get; }
    public ICommand SelectRecommendedCommand { get; }
    /// <summary>Instala de uma vez todas as recomendadas pelo Windows (uma confirmação, um aviso do Windows, um Desfazer).</summary>
    public ICommand InstallRecommendedCommand { get; }
    public string InstallRecommendedLabel => $"Instalar todas ({Recommended.Count(r => r.CanApply)})";

    public ObservableCollection<DriverUpdateRow> Recommended { get; } = [];
    public ObservableCollection<DriverUpdateRow> Optional { get; } = [];
    public ObservableCollection<DriverUpdateRow> Graphics { get; } = [];
    public ObservableCollection<GpuDriverCard> Gpus { get; } = [];

    /// <summary>Dispositivos sem driver ou com driver que não carrega (código de problema do Gerenciador de Dispositivos).</summary>
    public ObservableCollection<DeviceProblem> MissingDevices { get; } = [];
    public int MissingCount => MissingDevices.Count;
    public bool HasMissing => MissingDevices.Count > 0;
    public string MissingSummary { get => _missingSummary; private set => Set(ref _missingSummary, value); }
    private string _missingSummary = "";
    public string MissingAdvice => _missingAdvice;
    private readonly string _missingAdvice = DeviceProblems.Advice;

    public bool Searching { get => _searching; private set { if (Set(ref _searching, value)) NotifyOffers(); } }
    public bool Searched { get => _searched; private set { if (Set(ref _searched, value)) NotifyOffers(); } }
    public string SearchStatus { get => _searchStatus; private set { if (Set(ref _searchStatus, value)) OnPropertyChanged(nameof(HeroCaption)); } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string ErrorText { get => _error; private set => Set(ref _error, value); }
    public string RebootNotice { get => _reboot; private set => Set(ref _reboot, value); }
    public string GpuNote { get => _gpuNote; private set => Set(ref _gpuNote, value); }
    public string WhenToUpdateGpu => _whenGpu;
    public bool CanUndo => _lastBatch is not null;
    public string UndoLabel => _lastBatch is { } b ? $"Desfazer ({b.Label})" : "Desfazer";
    public int RecommendedCount => Recommended.Count;
    public int OptionalCount => Optional.Count;
    public int GraphicsCount => Graphics.Count;

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Atualizações encontradas na última busca (NaN antes de procurar): o número do destaque.</summary>
    public double UpdatesNumber => Searched ? Recommended.Count + Optional.Count + Graphics.Count : double.NaN;
    public double RecommendedNumber => Recommended.Count;
    public double OptionalNumber => Optional.Count;
    public double GraphicsNumber => Graphics.Count;
    /// <summary>Texto no lugar do número antes da primeira busca.</summary>
    public string HeroValueText => Searching ? "Procurando…" : Searched ? "" : "Não procurado";
    public string HeroCaption => Searching
        ? SearchStatus + " Pode mudar de página: a busca continua."
        : Searched
            ? "Achadas pelo Windows Update agora. Instalar guarda o driver atual para Desfazer."
            : "A busca usa o próprio Windows Update e leva de 30 s a alguns minutos.";
    public bool HasOffers => Recommended.Count + Optional.Count + Graphics.Count > 0;
    /// <summary>Procurou e não há nada: estado vazio "tudo em dia".</summary>
    public bool ShowNoUpdates => Searched && !Searching && !HasOffers;
    public bool ShowNotSearched => !Searched && !Searching;
    public bool HasGpus => Gpus.Count > 0;
    /// <summary>Lendo as placas de vídeo ao abrir (esqueleto no cartão da GPU).</summary>
    public bool LoadingGpus { get => _loadingGpus; private set => Set(ref _loadingGpus, value); }
    private bool _loadingGpus;

    internal static UiTone UiToneOf(string tone) => tone switch { "good" => UiTone.Good, "bad" => UiTone.Bad, "trace" => UiTone.Warn, "signal" => UiTone.Info, _ => UiTone.Neutral };

    private void NotifyOffers()
    {
        foreach (var name in new[] { nameof(UpdatesNumber), nameof(RecommendedNumber), nameof(OptionalNumber), nameof(GraphicsNumber), nameof(HeroValueText),
                     nameof(HeroCaption), nameof(HasOffers), nameof(ShowNoUpdates), nameof(ShowNotSearched) })
            OnPropertyChanged(name);
    }

    private IEnumerable<DriverUpdateRow> AllRows() => Recommended.Concat(Optional).Concat(Graphics);
    private List<DriverUpdateRow> Selected() => AllRows().Where(r => r.IsChecked && r.CanApply).ToList();

    public override async Task OnShownAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RunAsync(LoadLocalAsync);
    }

    /// <summary>Ao abrir: só o que é local e rápido (placas de vídeo e reinício pendente); a busca online é por botão.</summary>
    private async Task LoadLocalAsync()
    {
        Message = "";
        LoadingGpus = true;
        DriverUpdatesReading r;
        try { r = await Task.Run(() => DriverUpdatesReader.Scan(TimeSpan.Zero, CancellationToken.None, searchUpdates: false)); }
        finally { LoadingGpus = false; }
        FillGpus(r);
        FillReboot(r.RebootPending);
        FillProblems(r);
        Message = "";
        if (!_gpuChecked)
        {
            _gpuChecked = true;
            foreach (var card in Gpus.Where(c => c.CanCheck)) _ = CheckGpuAsync(card); // só consulta (não baixa nada)
        }
    }

    /// <summary>"Ver se falta algum driver": aparelhos sem driver ou com driver que não carrega (lidos ao abrir e a cada busca).</summary>
    private void FillProblems(DriverUpdatesReading r)
    {
        MissingDevices.Clear();
        foreach (var p in r.NeedingDriver) MissingDevices.Add(p);
        var disabled = r.Problems.Count(p => p.Kind == DeviceProblemKind.Disabled);
        var missing = r.NeedingDriver.Count(p => p.Kind == DeviceProblemKind.MissingDriver);
        var failed = r.NeedingDriver.Count - missing;
        MissingSummary = MissingDevices.Count == 0
            ? (r.ProblemCodesRead ? $"Nenhum dispositivo sem driver ({r.DeviceCount} conferidos)." : "")
            : $"{missing} sem driver, {failed} com driver que não carrega." +
              (disabled > 0 ? $" {disabled} desativado(s) por você ou pelo BIOS não entra(m) na conta." : "");
        OnPropertyChanged(nameof(MissingCount));
        OnPropertyChanged(nameof(HasMissing));
    }

    private async Task SearchAsync()
    {
        using var cts = new CancellationTokenSource();
        using var tick = new CancellationTokenSource();
        _cancel = cts.Cancel;
        var token = cts.Token;
        Searching = true;
        ErrorText = "";
        Message = "";
        var started = DateTime.Now;
        SearchStatus = "Procurando no Windows Update…";
        var ticker = Tick(started, tick.Token);
        try
        {
            var r = await Task.Run(() => DriverUpdatesReader.Scan(DriverUpdatesReader.DefaultTimeout, token), CancellationToken.None);
            FillGpus(r);
            FillReboot(r.RebootPending);
            FillProblems(r);
            if (r.Error != WuErrorKind.None)
            {
                ErrorText = r.ErrorMessage ?? "Não foi possível consultar o Windows Update.";
                Summary = r.Error == WuErrorKind.Canceled ? "Busca cancelada." : "A busca não terminou.";
                return;
            }
            FillOffers(r.Offers);
            Searched = true;
            var total = r.Offers.Count;
            Summary = total == 0
                ? "O Windows Update não tem atualização de driver para este PC agora. Isso é normal: o Windows só oferece versões testadas para o seu hardware."
                : $"{total} atualização(ões) de driver: {Recommended.Count} recomendada(s), {Optional.Count} opcional(is), {Graphics.Count} de vídeo.";
            if (r.InventoryNote is not null) Summary += " " + r.InventoryNote;
            if (MissingDevices.Count > 0 && !r.Offers.Any(o => o.InstalledVersion is null))
                Summary += $" O Windows Update não ofereceu driver para {MissingDevices.Count} dispositivo(s) sem driver: veja o site do fabricante (cartão \"Dispositivos sem driver\").";
            SearchStatus = $"Busca concluída em {(DateTime.Now - started).TotalSeconds:0} s, às {DateTime.Now:HH:mm}.";
        }
        finally
        {
            Searching = false;
            _cancel = null;
            await tick.CancelAsync(); // encerra o contador
            await ticker;
        }
    }

    private async Task Tick(DateTime started, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                if (Searching) SearchStatus = $"Procurando no Windows Update… {(DateTime.Now - started).TotalSeconds:0} s (até {DriverUpdatesReader.DefaultTimeout.TotalMinutes:0} min)";
            }
        }
        catch (OperationCanceledException) { }
    }

    private void FillReboot(bool? pending) =>
        RebootNotice = pending == true
            ? "O Windows está esperando um reinício (de uma atualização anterior). Reinicie antes de instalar mais drivers: alguns só valem depois disso."
            : "";

    private void FillGpus(DriverUpdatesReading r)
    {
        var previous = Gpus.ToList();
        if (previous.Any(c => c.Working)) return; // não troca o cartão no meio de um download/instalação
        Gpus.Clear();
        foreach (var g in r.Gpus)
        {
            var card = new GpuDriverCard { Status = g };
            card.OpenPageCommand = new RelayCommand(() => OpenVendorPage(g));
            card.OpenAppCommand = new RelayCommand(() => OpenVendorApp(g));
            card.CheckCommand = new RelayCommand(() => _ = CheckGpuAsync(card), () => card.CanCheck);
            card.InstallCommand = new RelayCommand(() => _ = InstallGpuAsync(card), () => card.CanInstall && !Gpus.Any(c => c.Working) && !Busy);
            card.CancelCommand = new RelayCommand(() => _gpuCancel?.Cancel(), () => card.Downloading);
            card.AutoDetectCommand = new RelayCommand(() => _ = AutoDetectAsync(card), () => card.HasAutoDetect && !card.Working);
            card.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertGpuAsync(card)), () => !Busy && card.CanRevert);
            if (previous.FirstOrDefault(p => p.Status.Vendor == g.Vendor && p.Status.Name == g.Name && p.Status.InstanceId == g.InstanceId) is { } old)
            {
                card.RunId = old.RunId;
                card.Installed = old.Installed;
                card.Result = old.Result;
                // Versão mudou (instalou): procura de novo para dizer "Em dia"; senão mantém o resultado.
                if (old.Status.WindowsVersion == g.WindowsVersion) card.Check = old.Check;
                else if (card.CanCheck) _ = CheckGpuAsync(card);
            }
            Gpus.Add(card);
        }
        GpuNote = r.GpuNote ?? "";
        OnPropertyChanged(nameof(HasGpus));
    }

    private void FillOffers(IReadOnlyList<DriverUpdateOffer> offers)
    {
        Recommended.Clear();
        Optional.Clear();
        Graphics.Clear();
        foreach (var o in offers)
        {
            var u = o.Update;
            var parts = new List<string>();
            if (u.DriverClass is not null) parts.Add(u.DriverClass);
            if ((u.Manufacturer ?? u.Provider) is { } m) parts.Add(m);
            if (u.DriverDate is { } d) parts.Add("de " + d.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture));
            parts.Add(DriverUpdatesReader.FormatSize(u.SizeBytes));
            if (u.KbArticles.Count > 0) parts.Add(string.Join(", ", u.KbArticles));
            if (o.DeviceCount > 1) parts.Add($"{o.DeviceCount} dispositivos");
            var row = new DriverUpdateRow
            {
                Offer = o,
                Detail = string.Join(" · ", parts),
                Badge = u.Optional ? "opcional" : "recomendada",
            };
            row.Tone = o.Blocked is not null ? "muted" : u.Optional ? "trace" : "good";
            row.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertRunAsync(row.RunId)), () => !Busy && row.CanRevert);
            row.DetailsCommand = new RelayCommand(() => OpenDetails(row));
            row.InstallCommand = new RelayCommand(() => _ = RunAsync(() => InstallAsync([row])), () => !Busy && row.CanApply);
            (o.Group switch { OfferGroup.Graphics => Graphics, OfferGroup.Optional => Optional, _ => Recommended }).Add(row);
        }
        OnPropertyChanged(nameof(RecommendedCount));
        OnPropertyChanged(nameof(OptionalCount));
        OnPropertyChanged(nameof(GraphicsCount));
        OnPropertyChanged(nameof(InstallRecommendedLabel));
        NotifyOffers();
    }

    // ------------------------------------------------------------------ instalar / desfazer

    private async Task InstallAsync(List<DriverUpdateRow> rows)
    {
        if (Host is null || rows.Count == 0) return;
        if (rows.Count > MaxBatch) { Message = $"Escolha no máximo {MaxBatch} atualizações por vez."; return; }
        var requests = rows.Select(r => new ActionRequest(InstallAction, Arguments: new Dictionary<string, string>
        {
            ["updateId"] = r.UpdateId,
            ["updateRevision"] = r.Revision,
        })).ToList();
        foreach (var row in rows) { row.Status = "instalando pelo Windows Update…"; row.Tone = "signal"; }
        var result = await Host.ApplyAsync(rows.Count == 1 ? "Instalar driver" : $"Instalar drivers ({rows.Count})",
            DriverUpdatesReader.ConfirmationText(rows.Select(r => r.Offer).ToList()), requests, BrokerLevel.Admin);
        if (result is null)
        {
            foreach (var row in rows) { row.Status = ""; row.Tone = "muted"; }
            return;
        }
        var results = result.Response?.Results ?? [];
        var done = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var res = results.FirstOrDefault(x => x.Index == i) ?? (rows.Count == 1 && results.Count > 0 ? results[0] : null);
            if (res is { Outcome: "Verified" })
            {
                row.RunId = result.Response?.RunId;
                row.Applied = true;
                row.Status = "instalada · driver anterior guardado";
                row.Tone = "good";
                done++;
            }
            else
            {
                row.Status = res is null ? result.Message : $"{Translate(res.Outcome)}: {res.Detail}";
                row.Tone = "bad";
            }
        }
        if (done > 0 && result.Response?.RunId is { } run)
        {
            _lastBatch = (run, $"{done} driver(s)");
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(UndoLabel));
        }
        var reboot = await Task.Run(() => { try { return WindowsRebootState.Pending(new WuaAgent()); } catch (Exception ex) when (ex is not OutOfMemoryException) { return null; } });
        FillReboot(reboot);
        Message = (done == rows.Count ? $"{done} driver(s) instalado(s)." : $"{done} de {rows.Count} instalado(s); veja o motivo em cada linha.") +
                  (reboot == true ? " Reinicie o PC para concluir." : "");
        OnPropertyChanged(nameof(InstallRecommendedLabel));
    }

    private async Task UndoBatchAsync()
    {
        if (_lastBatch is { } b) await RevertRunAsync(b.Run);
    }

    /// <summary>Desfazer: o diário é por execução, então volta todas as atualizações instaladas naquele pedido.</summary>
    private async Task RevertRunAsync(Guid? runId)
    {
        if (Host is null || runId is not { } run) return;
        var rows = AllRows().Where(x => x.RunId == run).ToList();
        var r = await Host.RevertAsync(run, BrokerLevel.Admin);
        if (r is null) return;
        var results = r.Response?.Results ?? [];
        var all = r.Response?.FullyRestored == true;
        foreach (var row in rows)
        {
            var res = results.FirstOrDefault(x => row.Matches(x.Target));
            if (all || res is { Outcome: "Reverted" or "AlreadyOriginal" or "NothingToRevert" })
            {
                row.Applied = false;
                row.RunId = null;
                row.Status = "driver anterior reinstalado";
                row.Tone = "signal";
            }
            else
            {
                row.Status = $"não voltou: {res?.Detail ?? r.Message}";
                row.Tone = "bad";
            }
        }
        if (all && _lastBatch?.Run == run)
        {
            _lastBatch = null;
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(UndoLabel));
        }
        Message = all ? "Drivers anteriores reinstalados. Se algo ainda estiver estranho, reinicie o PC."
                      : "Desfazer não terminou para todos: veja o motivo em cada linha (ou use a Restauração do Sistema).";
    }

    private static string Translate(string outcome) => outcome switch
    {
        "Blocked" => "bloqueado",
        "NotApplicable" => "não se aplica",
        "FailedAndCompensated" => "falhou (nada mudou)",
        "Failed" => "falhou",
        "Interrupted" => "interrompido (veja Aplicar > Recuperar)",
        "SkippedExternalChange" => "mudou enquanto isso; nada feito",
        "AlreadyConfigured" => "já estava instalada",
        _ => outcome,
    };

    // ------------------------------------------------------------------ placa de vídeo: procurar, baixar, instalar (no NEXUS)

    private GpuDriverUpdater Updater() => _updater ??= new GpuDriverUpdater(new VendorDriverClient(), new WindowsAuthenticodeVerifier());

    private int WindowsBuild => Host?.Facts?.WindowsBuild ?? Environment.OSVersion.Version.Build;

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static long? FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"Procurar versão nova": consulta oficial do fabricante (só leitura).</summary>
    private async Task CheckGpuAsync(GpuDriverCard card)
    {
        if (!card.CanCheck) return;
        card.Checking = true;
        card.Error = "";
        try { card.Check = await Updater().CheckAsync(card.Status, WindowsBuild, CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { card.Check = new GpuUpdateCheck(GpuUpdateState.Failed, "Não deu para consultar o fabricante: " + ex.Message); }
        finally { card.Checking = false; }
    }

    /// <summary>
    /// "Baixar e instalar": baixa do servidor oficial (progresso, velocidade, tempo restante), confere SHA-256 e assinatura, e instala
    /// pelo aplicador (Host.ApplyAsync, administrador, ponto de restauração; o aplicador confere tudo de novo antes de executar).
    /// </summary>
    private async Task InstallGpuAsync(GpuDriverCard card)
    {
        if (Host is null || card.Check is not { State: GpuUpdateState.Available, Offer: { } offer } check || card.Working) return;
        card.Working = true;
        card.Error = "";
        card.Result = "";
        using var cts = new CancellationTokenSource();
        _gpuCancel = cts;
        DownloadedInstaller? file = null;
        try
        {
            card.Downloading = true;
            card.Phase = "Baixando do servidor oficial da " + GpuIdentity.VendorName(offer.Vendor) + "…";
            var progress = new Progress<VendorDownloadProgress>(p =>
            {
                card.Progress = p.Fraction;
                card.ProgressText = GpuDriverUpdater.ProgressText(p);
            });
            file = await Updater().DownloadAsync(check, UserProfile, progress, () => card.Phase = "Conferindo assinatura…", FreeSpace, cts.Token);
            card.Downloading = false;
            card.ProgressText = "";
            card.Phase = $"Assinatura conferida: {file.Signer}. Instalando pelo aplicador…";
            var request = new ActionRequest(GpuInstallAction, Arguments: new Dictionary<string, string>
            {
                ["gpuVendor"] = VendorDriverPolicy.VendorToken(file.Vendor),
                ["gpuVersion"] = file.Version,
                ["installerSha256"] = file.Sha256,
            });
            var result = await Host.ApplyAsync($"Instalar driver {GpuIdentity.VendorName(file.Vendor)} {file.Version}",
                GpuDriverUpdater.ConfirmationText(card.Status, file, check.Note), [request], BrokerLevel.Admin);
            if (result is null) { card.Phase = ""; card.Result = "Instalação cancelada. O instalador baixado fica guardado para a próxima vez."; return; }
            var res = result.Response?.Results is { Count: > 0 } list ? list[0] : null;
            if (res is { Outcome: "Verified" or "AlreadyConfigured" })
            {
                card.RunId = result.Response?.RunId;
                card.Installed = true;
                card.Result = res.Outcome == "Verified" ? $"Driver {file.Version} instalado. Driver anterior guardado para Desfazer." : $"O driver {file.Version} já estava instalado.";
                GpuDriverUpdater.Discard(file);
                var reboot = await Task.Run(() => { try { return WindowsRebootState.Pending(new WuaAgent()); } catch (Exception ex) when (ex is not OutOfMemoryException) { return null; } });
                FillReboot(reboot);
                if (reboot == true) card.Result += " Reinicie o PC para concluir.";
            }
            else
            {
                card.Error = res is null ? result.Message : $"{Translate(res.Outcome)}: {res.Detail}";
            }
        }
        catch (OperationCanceledException) { card.Error = ""; card.Result = "Download pausado. \"Baixar e instalar\" continua de onde parou."; }
        catch (VendorDriverException ex) { card.Error = ex.Message; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { card.Error = "Não foi possível concluir: " + ex.Message; }
        finally
        {
            card.Downloading = false;
            card.ProgressText = "";
            card.Phase = "";
            card.Working = false;
            _gpuCancel = null;
        }
        if (card.Installed) await RunAsync(LoadLocalAsync); // relê a versão instalada (o cartão diz "Em dia")
    }

    /// <summary>AMD sem ramo identificado: baixa o "Auto-Detect and Install" oficial e o ABRE (o usuário vê e controla o instalador da AMD).</summary>
    private async Task AutoDetectAsync(GpuDriverCard card)
    {
        if (Host is null || card.Check?.AutoDetectUrl is not { } url || card.Working) return;
        if (!Host.Confirm("Assistente oficial da AMD", "Baixar o \"Auto-Detect and Install\" do servidor oficial da AMD (~50 MB) e abrir?\n\n" +
                "O NEXUS confere a assinatura digital da AMD antes de abrir. A janela do instalador da AMD aparece e você escolhe o que instalar " +
                "(é o instalador da AMD, não o NEXUS: o Desfazer do NEXUS não cobre essa instalação; o próprio Windows mantém o driver anterior em Gerenciador de Dispositivos > Reverter driver).")) return;
        card.Working = true;
        card.Downloading = true;
        card.Error = "";
        using var cts = new CancellationTokenSource();
        _gpuCancel = cts;
        try
        {
            card.Phase = "Baixando o assistente da AMD…";
            var progress = new Progress<VendorDownloadProgress>(p => { card.Progress = p.Fraction; card.ProgressText = GpuDriverUpdater.ProgressText(p); });
            var path = await Updater().DownloadAutoDetectAsync(url, UserProfile, progress, cts.Token);
            card.Phase = "Conferindo assinatura…";
            await Task.Run(() => AmdAutoDetectTool.Launch(path, new WindowsAuthenticodeVerifier()));
            card.Result = "Assistente da AMD aberto. Siga a janela dele; depois volte aqui e clique em Procurar versão nova.";
        }
        catch (OperationCanceledException) { card.Result = "Download cancelado."; }
        catch (Exception ex) when (ex is VendorDriverException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { card.Error = ex.Message; }
        finally
        {
            card.Downloading = false;
            card.ProgressText = "";
            card.Phase = "";
            card.Working = false;
            _gpuCancel = null;
        }
    }

    private async Task RevertGpuAsync(GpuDriverCard card)
    {
        if (Host is null || card.RunId is not { } run) return;
        var r = await Host.RevertAsync(run, BrokerLevel.Admin);
        if (r is null) return;
        if (r.Response?.FullyRestored == true)
        {
            card.Installed = false;
            card.RunId = null;
            card.Result = "Driver anterior reinstalado. Se a tela ficar estranha, reinicie o PC.";
            card.Error = "";
            await LoadLocalAsync();
        }
        else
        {
            var detail = r.Response?.Results is { Count: > 0 } list ? list[0].Detail : null;
            card.Error = "Desfazer não terminou: " + (detail ?? r.Message) + " Use a Restauração do Sistema (ponto criado antes de instalar).";
        }
    }

    // ------------------------------------------------------------------ abrir (exceção controlada, depois de confirmar)

    private void OpenDetails(DriverUpdateRow row)
    {
        var url = row.Offer.Update.DetailsUrl ?? WindowsUpdateQueries.CatalogUrl(row.UpdateId);
        if (Host?.Confirm("Detalhes da atualização", $"Abrir no navegador a página oficial da Microsoft sobre \"{row.UpdateTitle}\"?\n\n{url}") != true) return;
        try { DriverVendorTools.OpenDetails(url); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException) { Message = "Não foi possível abrir: " + ex.Message; }
    }

    private void OpenVendorPage(GpuDriverStatus g)
    {
        if (g.OfficialPage is null) return;
        if (Host?.Confirm("Página oficial de drivers", $"Abrir no navegador a página oficial de drivers da {GpuIdentity.VendorName(g.Vendor)}?\n\n{g.OfficialPage}\n\n" +
                "Não precisa: o botão \"Baixar e instalar\" do NEXUS já baixa do servidor oficial e instala. Use a página para ver notas de versão ou escolher outra versão.") != true) return;
        try { DriverVendorTools.OpenOfficialDriverPage(g.Vendor); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException) { Message = "Não foi possível abrir: " + ex.Message; }
    }

    private void OpenVendorApp(GpuDriverStatus g)
    {
        if (g.App is not { } app) return;
        if (Host?.Confirm(app.Name, $"Abrir o {app.Name}? É o app oficial do fabricante, já instalado no PC; nele você vê e instala drivers da placa de vídeo.") != true) return;
        try { DriverVendorTools.OpenVendorApp(app); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { Message = "Não foi possível abrir: " + ex.Message; }
    }

    private void OpenWindowsUpdate()
    {
        try { DriverVendorTools.OpenWindowsUpdateSettings(optional: true); }
        catch (System.ComponentModel.Win32Exception)
        {
            try { DriverVendorTools.OpenWindowsUpdateSettings(optional: false); }
            catch (System.ComponentModel.Win32Exception ex) { Message = "Não foi possível abrir: " + ex.Message; }
        }
    }
}
