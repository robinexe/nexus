using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Nexus.Broker;
using Nexus.DriverUpdates;
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

    /// <summary>O alvo do diário ("revision=N | updateId=…") é desta linha.</summary>
    public bool Matches(string? target) => target is not null && target.Split(" | ").Contains("updateId=" + UpdateId, StringComparer.Ordinal);
}

/// <summary>Placa de vídeo: versão e idade do driver, com a página oficial e o app oficial (se instalado).</summary>
public sealed class GpuDriverCard
{
    public required GpuDriverStatus Status { get; init; }
    public string Name => Status.Name;
    public string Kind => (Status.Integrated ? "integrada" : "dedicada") + " · " + GpuIdentity.VendorName(Status.Vendor);
    public string Version => Status.VendorVersion is { } v ? $"{v}  ({Status.WindowsVersion})" : Status.WindowsVersion.Length > 0 ? Status.WindowsVersion : "n/d";
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
    public ICommand? OpenPageCommand { get; set; }
    public ICommand? OpenAppCommand { get; set; }
}

/// <summary>
/// Atualizar drivers: procura no Windows Update (leitura, direto da biblioteca, com tempo limite e cancelamento), mostra o que
/// cada oferta troca e instala as escolhidas SÓ pelo aplicador (Host.ApplyAsync, administrador, ponto de restauração automático).
/// Placa de vídeo: versão e idade do driver, com a página oficial do fabricante (só abre, depois de confirmar).
/// </summary>
public sealed class DriverUpdatesPage : ModulePage
{
    public override string Title => "Atualizar drivers";
    public override string Group => "Sistema";
    public override string Subtitle => "Drivers oficiais pelo Windows Update e a placa de vídeo pelo site do fabricante.";

    private const string InstallAction = "driverupdates.install@1";
    public const int MaxBatch = BrokerService.MaxActionsPerRequest;

    private bool _loaded, _searching, _searched;
    private string _searchStatus = "", _summary = "Clique em Procurar atualizações. A busca usa o próprio Windows Update e pode levar alguns minutos.";
    private string _error = "", _reboot = "", _gpuNote = "";
    private Action? _cancel;
    private readonly string _whenGpu = GpuDrivers.WhenToUpdate;
    private (Guid Run, string Label)? _lastBatch;

    public DriverUpdatesPage()
    {
        SearchCommand = new RelayCommand(() => _ = RunAsync(SearchAsync), () => !Busy);
        CancelCommand = new RelayCommand(() => _cancel?.Invoke(), () => _searching);
        InstallSelectedCommand = new RelayCommand(() => _ = RunAsync(() => InstallAsync(Selected())), () => !Busy && Selected().Count > 0);
        UndoCommand = new RelayCommand(() => _ = RunAsync(UndoBatchAsync), () => !Busy && _lastBatch is not null);
        OpenWindowsUpdateCommand = new RelayCommand(OpenWindowsUpdate);
        SelectRecommendedCommand = new RelayCommand(() => { foreach (var r in Recommended.Where(r => r.CanApply)) r.IsChecked = true; });
    }

    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand InstallSelectedCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand OpenWindowsUpdateCommand { get; }
    public ICommand SelectRecommendedCommand { get; }

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
        Gpus.Clear();
        foreach (var g in r.Gpus)
        {
            var card = new GpuDriverCard { Status = g };
            card.OpenPageCommand = new RelayCommand(() => OpenVendorPage(g));
            card.OpenAppCommand = new RelayCommand(() => OpenVendorApp(g));
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
            (o.Group switch { OfferGroup.Graphics => Graphics, OfferGroup.Optional => Optional, _ => Recommended }).Add(row);
        }
        OnPropertyChanged(nameof(RecommendedCount));
        OnPropertyChanged(nameof(OptionalCount));
        OnPropertyChanged(nameof(GraphicsCount));
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
                "O NEXUS não baixa nada: você escolhe a versão no site do fabricante. Prefira a versão 'Game Ready'/estável mais recente.") != true) return;
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
