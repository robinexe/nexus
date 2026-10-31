using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions.Registry;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Limpeza, em quatro abas:
/// <list type="bullet">
/// <item>Limpar: mede (somente leitura, aqui mesmo) o que cada regra recupera e pede a limpeza ao aplicador (Host.ApplyAsync).
/// Tudo vai para a quarentena e pode voltar (Host.RevertAsync). Lixeira e ferramentas oficiais do Windows ficam ao lado.</item>
/// <item>Quarentena: lotes, desfazer e ESVAZIAR (irreversível: duas confirmações com a lista e o tamanho exato, pedido com
/// ConfirmIrreversible). Expiração opcional (desligada por padrão) só SUGERE esvaziar.</item>
/// <item>Histórico: sessões, totais (recuperado de verdade = esvaziado; na quarentena = recuperável), meses, CSV.</item>
/// <item>Regras: regras do usuário em %AppData%\NEXUS\rules, com erros por arquivo, abrir pasta e recarregar.</item>
/// </list>
/// A página nunca move nem apaga arquivos do sistema: só grava as próprias preferências, o exemplo de regra e o CSV que o usuário pede.
/// </summary>
public sealed class CleaningPage : ModulePage
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private const string UserRuleActionKey = CleaningUserRuleHandler.ActionId + "@1";
    private const int MaxPerRequest = 64;

    private CancellationTokenSource? _scanCancel;
    private bool _scanning;
    private bool _scanned;
    private string _tab = "clean";
    private string _recoverableText = "—";
    private string _recoverableCaption = "Medindo o que pode ser liberado…";
    private string _selectedText = "";
    private string _quarantineText = "Quarentena vazia.";
    private string _openApps = "";
    private string _historyEmptyText = "Nenhuma limpeza ainda.";
    private string _recycleText = "—";
    private string _expirySuggestion = "";
    private string _rulesSummary = "";
    private string _rulesFolderProblem = "";
    private CleaningHistoryReport? _report;
    private UserRuleLoad _userRules = UserRuleLoad.None("");
    private CleaningSettings _settings = CleaningSettings.Load();
    private CleaningOption _purgeAge;
    private CleaningOption _expiry;
    private RecycleDriveOption? _recycleDrive;
    private double _recoverableBytes = double.NaN;
    private double _selectedBytes;
    private double _freeBytes = double.NaN;
    private string _systemDrive = "C:";

    public CleaningPage()
    {
        PurgeAgeOptions = [new("7 dias", 7), new("14 dias", 14), new("30 dias", 30), new("60 dias", 60), new("90 dias", 90)];
        ExpiryOptions = [new("Desligado", 0), new("7 dias", 7), new("14 dias", 14), new("30 dias", 30), new("60 dias", 60), new("90 dias", 90)];
        _purgeAge = PurgeAgeOptions[2];
        _expiry = ExpiryOptions.FirstOrDefault(o => o.Value == _settings.QuarantineExpiryDays) ?? ExpiryOptions[0];

        RescanCommand = new RelayCommand(() => _ = ScanAsync(), () => !_scanning && !Busy);
        CancelScanCommand = new RelayCommand(() => _scanCancel?.Cancel(), () => _scanning);
        CleanCommand = new RelayCommand(() => _ = RunAsync(CleanSelectedAsync), () => !Busy && !_scanning && Host is not null && SelectedRows().Any());
        UndoLastCommand = new RelayCommand(() => _ = RunAsync(() => RestoreAsync(Batches.FirstOrDefault(h => h.CanRestore))), () => !Busy && Batches.Any(h => h.CanRestore));
        EmptyRecycleBinCommand = new RelayCommand(() => _ = RunAsync(EmptyRecycleBinAsync), () => !Busy && Host is not null && _recycleDrive is { Bytes: > 0 });
        OpenRecycleBinCommand = new RelayCommand(() => TryOpen("shell:RecycleBinFolder"));
        OpenStorageSettingsCommand = new RelayCommand(() => TryOpen("ms-settings:storagesense"));
        OpenDiskCleanupCommand = new RelayCommand(OpenDiskCleanup, () => Host is not null);
        PurgeAllCommand = new RelayCommand(() => _ = RunAsync(() => PurgeAsync(Batches.Where(b => b.CanPurge).ToList(), "toda a quarentena")), () => !Busy && Host is not null && Batches.Any(b => b.CanPurge));
        PurgeOlderCommand = new RelayCommand(() => _ = RunAsync(() => PurgeOlderAsync(_purgeAge.Value)), () => !Busy && Host is not null && Batches.Any(b => b.CanPurge));
        PurgeExpiredCommand = new RelayCommand(() => _ = RunAsync(() => PurgeOlderAsync(_settings.QuarantineExpiryDays)), () => !Busy && Host is not null && HasExpirySuggestion);
        RefreshCommand = new RelayCommand(() => _ = RunAsync(LoadHistoryAsync), () => !Busy);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => _report is { Sessions.Count: > 0 } || _report is { RecycleBin.Count: > 0 });
        OpenRulesFolderCommand = new RelayCommand(OpenRulesFolder);
        ReloadRulesCommand = new RelayCommand(() => _ = ReloadRulesAsync(), () => !_scanning && !Busy);
        ShowCleanCommand = new RelayCommand(() => Tab = "clean");
    }

    public override string Title => "Limpeza";
    public override string Group => "Limpar";
    public override string Subtitle => "Libere espaço com segurança: o que sai vai para uma quarentena e pode voltar até você esvaziá-la.";

    public ObservableCollection<CleaningCategoryGroup> Categories { get; } = [];
    public ObservableCollection<QuarantineBatchRow> Batches { get; } = [];
    public ObservableCollection<HistorySessionRow> Sessions { get; } = [];
    public ObservableCollection<MonthBar> Months { get; } = [];
    public ObservableCollection<UserRuleFileRow> RuleFiles { get; } = [];
    public ObservableCollection<RecycleDriveOption> RecycleDrives { get; } = [];
    public IReadOnlyList<CleaningOption> PurgeAgeOptions { get; }
    public IReadOnlyList<CleaningOption> ExpiryOptions { get; }

    public RelayCommand RescanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand CleanCommand { get; }
    public RelayCommand UndoLastCommand { get; }
    public RelayCommand EmptyRecycleBinCommand { get; }
    public RelayCommand OpenRecycleBinCommand { get; }
    public RelayCommand OpenStorageSettingsCommand { get; }
    public RelayCommand OpenDiskCleanupCommand { get; }
    public RelayCommand PurgeAllCommand { get; }
    public RelayCommand PurgeOlderCommand { get; }
    public RelayCommand PurgeExpiredCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand OpenRulesFolderCommand { get; }
    public RelayCommand ReloadRulesCommand { get; }
    public RelayCommand ShowCleanCommand { get; }

    // ------------------------------------------------------------------ abas

    public string Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            OnPropertyChanged(nameof(IsCleanTab));
            OnPropertyChanged(nameof(IsQuarantineTab));
            OnPropertyChanged(nameof(IsHistoryTab));
            OnPropertyChanged(nameof(IsRulesTab));
        }
    }

    public bool IsCleanTab { get => _tab == "clean"; set { if (value) Tab = "clean"; } }
    public bool IsQuarantineTab { get => _tab == "quarantine"; set { if (value) Tab = "quarantine"; } }
    public bool IsHistoryTab { get => _tab == "history"; set { if (value) Tab = "history"; } }
    public bool IsRulesTab { get => _tab == "rules"; set { if (value) Tab = "rules"; } }

    // ------------------------------------------------------------------ textos

    public bool Scanning { get => _scanning; private set => Set(ref _scanning, value); }
    public string RecoverableText { get => _recoverableText; private set => Set(ref _recoverableText, value); }
    public string RecoverableCaption { get => _recoverableCaption; private set => Set(ref _recoverableCaption, value); }
    public string SelectedText { get => _selectedText; private set => Set(ref _selectedText, value); }
    public string QuarantineText { get => _quarantineText; private set => Set(ref _quarantineText, value); }
    public string OpenAppsWarning { get => _openApps; private set { if (Set(ref _openApps, value)) OnPropertyChanged(nameof(HasOpenApps)); } }
    public bool HasOpenApps => OpenAppsWarning.Length > 0;
    public string HistoryEmptyText { get => _historyEmptyText; private set => Set(ref _historyEmptyText, value); }
    public bool HasHistory => Sessions.Count > 0;
    public string RecycleText { get => _recycleText; private set => Set(ref _recycleText, value); }
    public string ExpirySuggestion { get => _expirySuggestion; private set { if (Set(ref _expirySuggestion, value)) OnPropertyChanged(nameof(HasExpirySuggestion)); } }
    public bool HasExpirySuggestion => _expirySuggestion.Length > 0;
    public string RulesSummary { get => _rulesSummary; private set => Set(ref _rulesSummary, value); }
    public string RulesFolderProblem { get => _rulesFolderProblem; private set => Set(ref _rulesFolderProblem, value); }
    public string RulesFolder { get; } = UserRulesFolder ?? "(pasta de dados do Windows indisponível)";

    // Números para a apresentação (design system v2): o HeroPanel conta, o BeforeAfter compara, os StatTiles do histórico.
    /// <summary>Total recuperável medido agora (NaN antes da primeira medição).</summary>
    public double RecoverableBytes { get => _recoverableBytes; private set => Set(ref _recoverableBytes, value); }
    /// <summary>Soma dos itens marcados.</summary>
    public double SelectedBytes { get => _selectedBytes; private set { if (Set(ref _selectedBytes, value)) OnPropertyChanged(nameof(FreeAfterBytes)); } }
    /// <summary>Espaço livre agora no disco do Windows (somente leitura; NaN se não deu para ler).</summary>
    public double FreeBytes { get => _freeBytes; private set { if (Set(ref _freeBytes, value)) OnPropertyChanged(nameof(FreeAfterBytes)); } }
    /// <summary>Livre depois de limpar os marcados e esvaziar a quarentena (o espaço só volta de verdade ao esvaziar).</summary>
    public double FreeAfterBytes => double.IsFinite(FreeBytes) ? FreeBytes + SelectedBytes : double.NaN;
    public string SystemDrive { get => _systemDrive; private set => Set(ref _systemDrive, value); }
    public bool HasBatches => Batches.Count > 0;
    public double RecoveredMonthBytes => _report is null ? double.NaN : _report.RecoveredThisMonth;
    public double RecoveredTotalBytes => _report is null ? double.NaN : _report.RecoveredTotal;
    public double RecoverableQuarantineBytes => _report is null ? double.NaN : _report.Recoverable;
    public double CleanedMonthBytes => _report is null ? double.NaN : _report.CleanedThisMonth;

    public string RecoveredMonthText => _report is null ? "—" : Bytes(_report.RecoveredThisMonth);
    public string RecoveredTotalText => _report is null ? "—" : Bytes(_report.RecoveredTotal);
    public string RecoverableQuarantineText => _report is null ? "—" : Bytes(_report.Recoverable);
    public string CleanedMonthText => _report is null ? "—" : Bytes(_report.CleanedThisMonth);
    public string RestoredTotalText => _report is null ? "—" : Bytes(_report.RestoredTotal);

    public string QuarantineNote { get; } =
        "Os arquivos limpos ficam na quarentena, no mesmo disco: o espaço só volta de verdade quando ela é esvaziada. " +
        "Esvaziar apaga de vez e não tem desfazer; por isso pede duas confirmações, com a lista e o tamanho exato.";

    public string DiskCleanupNote { get; } =
        "Limpeza de atualizações do Windows (pasta WinSxS): o próprio Windows já faz isso sozinho, de tempos em tempos (tarefa " +
        "StartComponentCleanup, que espera 30 dias antes de remover versões antigas). Para fazer agora, use a Limpeza de Disco do " +
        "Windows (você vê e escolhe). O NEXUS não roda DISM /StartComponentCleanup: remove na hora as versões antigas, sem a espera " +
        "de 30 dias, e as atualizações instaladas deixam de poder ser desinstaladas.";

    public CleaningOption PurgeAge { get => _purgeAge; set { if (value is not null) Set(ref _purgeAge, value); } }

    public CleaningOption Expiry
    {
        get => _expiry;
        set
        {
            if (value is null || !Set(ref _expiry, value)) return;
            _settings = _settings with { QuarantineExpiryDays = value.Value };
            _settings.Save();
            UpdateExpirySuggestion();
        }
    }

    public RecycleDriveOption? RecycleDrive { get => _recycleDrive; set => Set(ref _recycleDrive, value); }

    public override Task OnShownAsync() => _scanned || _scanning ? Task.CompletedTask : ScanAsync();

    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : "0.0", Pt) + " " + units[u];
    }

    private static string UserDataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.User))!;
    private static string AdminDataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.Admin))!;
    internal static string DataDirectory(BrokerLevel level) => level == BrokerLevel.User ? UserDataDirectory : AdminDataDirectory;

    private static string? UserRulesFolder
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "NEXUS", "rules");
        }
    }

    private IEnumerable<CleaningRuleRow> AllRows() => Categories.SelectMany(c => c.Rows);
    private IEnumerable<CleaningRuleRow> SelectedRows() => AllRows().Where(r => r.IsSelected && r.CanClean);

    internal void SelectionChanged()
    {
        var selected = SelectedRows().ToList();
        SelectedBytes = selected.Sum(r => r.Bytes);
        SelectedText = selected.Count == 0 ? "Nada selecionado."
            : $"{selected.Count} item(ns) selecionado(s): {Bytes(selected.Sum(r => r.Bytes))}" + (selected.Any(r => !r.Measured) ? " + itens sem medição" : "") + ".";
        foreach (var c in Categories) c.Refresh();
    }

    // ------------------------------------------------------------------ medição (somente leitura)

    private async Task ScanAsync()
    {
        _scanCancel?.Cancel();
        using var cancel = new CancellationTokenSource();
        _scanCancel = cancel;
        Scanning = true;
        RecoverableCaption = "Medindo o que pode ser liberado…";
        try
        {
            var userRules = await Task.Run(() => UserCleaningRules.Load(UserRulesFolder), cancel.Token);
            ApplyUserRules(userRules);
            var rules = CleaningRuleSet.BuiltIn.Rules.Concat(userRules.Rules.Rules).ToList();
            var folders = WindowsCleaningFolders.ForCurrentProcess(new WindowsRegistryBackend()) with
            {
                ExtraForbidden = [Path.Combine(UserDataDirectory, "quarantine"), Path.Combine(AdminDataDirectory, "quarantine")],
            };
            var results = await Task.Run(() => CleaningScanner.ScanAsync(rules, folders, SystemProcessProbe.Instance, DateTime.UtcNow,
                RecycleBinInfo.TryQuery, cancel: cancel.Token), cancel.Token);
            Populate(results);
            _scanned = true;
            await MeasureRecycleBinAsync();
            await LoadHistoryAsync();
            await MeasureFreeSpaceAsync();
        }
        catch (OperationCanceledException) { RecoverableCaption = "Medição cancelada."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { RecoverableCaption = $"Não foi possível medir: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_scanCancel, cancel)) _scanCancel = null;
            Scanning = false;
        }
    }

    /// <summary>Espaço livre do disco do Windows (só leitura, DriveInfo).</summary>
    private async Task MeasureFreeSpaceAsync()
    {
        var read = await Task.Run(() =>
        {
            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(root)) return ((double, string)?)null;
                var d = new DriveInfo(root);
                return d.IsReady ? ((double, string)?)(d.AvailableFreeSpace, root.TrimEnd('\\')) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        });
        FreeBytes = read?.Item1 ?? double.NaN;
        if (read is { } r) SystemDrive = r.Item2;
    }

    private static string CategoryName(RuleScanResult r) => r.Rule.IsUserRule ? "Suas regras" : r.Rule.Category switch
    {
        CleaningCategory.Sistema => "Sistema",
        CleaningCategory.Navegadores => "Navegadores",
        CleaningCategory.Apps => "Apps",
        CleaningCategory.Jogos => "Jogos",
        _ => "Desenvolvedor (desmarcado por padrão)",
    };

    private static int CategoryOrder(RuleScanResult r) => r.Rule.IsUserRule ? 99 : (int)r.Rule.Category;

    private void Populate(IReadOnlyList<RuleScanResult> results)
    {
        var previous = AllRows().ToDictionary(r => r.Rule.QuarantineKey, r => r.IsSelected, StringComparer.Ordinal);
        Categories.Clear();
        // A Lixeira tem cartão próprio (esvaziar é irreversível); aqui ficam só as regras.
        foreach (var group in results.Where(r => r.Rule.Special is null).GroupBy(CategoryName).OrderBy(g => CategoryOrder(g.First())))
        {
            var cat = new CleaningCategoryGroup(this, group.Key);
            var missing = new List<string>();
            foreach (var r in group)
            {
                // Programa não instalado (nenhuma pasta encontrada e nada a medir): só aparece na lista de ausentes.
                if (!r.Rule.IsUserRule && r.RootsFound == 0 && r.Problem is null) { missing.Add(r.Rule.Title.Split(':')[0]); continue; }
                var row = new CleaningRuleRow(this, r);
                row.SetSelected(previous.TryGetValue(r.Rule.QuarantineKey, out var was) ? was : r.Rule.DefaultSelected && row.CanClean && (r.Bytes > 0 || !r.Measured));
                cat.Rows.Add(row);
            }
            cat.NotFoundText = missing.Count == 0 ? "" : "Não encontrados neste PC: " + string.Join(", ", missing.Distinct(StringComparer.CurrentCulture)) + ".";
            if (cat.Rows.Count > 0 || missing.Count > 0) Categories.Add(cat);
        }
        var cleanable = AllRows().Where(r => r.CanClean).ToList();
        var total = cleanable.Sum(r => r.Bytes);
        RecoverableText = Bytes(total);
        RecoverableBytes = total;
        var unmeasured = cleanable.Count(r => !r.Measured);
        RecoverableCaption = "recuperáveis com segurança" + (unmeasured > 0 ? $" (mais {unmeasured} item(ns) que só o administrador consegue medir)" : "") +
                             ". Medido agora, sem mexer em nada.";
        var open = results.SelectMany(r => r.RunningApps.Select(a => (App: a, r.Rule))).Where(x => x.Rule.Mode == CleaningMode.Quarantine).ToList();
        OpenAppsWarning = open.Count == 0 ? "" :
            "Abertos agora: " + string.Join(", ", open.Select(x => x.App).Distinct(StringComparer.OrdinalIgnoreCase)) +
            ". O cache deles será pulado. Feche-os (inclusive na bandeja) e meça de novo para incluí-los.";
        SelectionChanged();
    }

    // ------------------------------------------------------------------ limpar (sempre pelo aplicador)

    private static ActionRequest RequestFor(CleaningRule rule, string batch) => rule.IsUserRule
        ? new ActionRequest(UserRuleActionKey, null, new Dictionary<string, string> { ["rule"] = rule.Id, ["batch"] = batch })
        : new ActionRequest(rule.ActionKey, null, new Dictionary<string, string> { ["batch"] = batch });

    private async Task CleanSelectedAsync()
    {
        var host = Host!;
        var selected = SelectedRows().ToList();
        if (selected.Count == 0) return;
        var sensitive = selected.Where(r => r.Rule.Sensitive).ToList();
        if (sensitive.Count > 0 && !host.Confirm("Atenção: dados pessoais",
                "Você marcou itens que não são cache:\n\n" + string.Join("\n\n", sensitive.Select(r => $"• {r.Rule.Title}\n  {r.Rule.Warning}")) +
                "\n\nContinuar mesmo assim?"))
        {
            Message = "Cancelado. Nada foi alterado.";
            return;
        }

        var batch = CleaningBatchId.New(DateTime.UtcNow);
        // Um pedido por nível (usuário primeiro) e no máximo 64 ações por pedido.
        var parts = selected.GroupBy(r => r.Rule.Admin).OrderBy(g => g.Key)
            .SelectMany(g => g.Chunk(MaxPerRequest).Select(c => (Admin: g.Key, Rows: c.ToList()))).ToList();
        var lines = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i].Rows;
            var level = parts[i].Admin ? BrokerLevel.Admin : BrokerLevel.User;
            var requests = part.Select(r => RequestFor(r.Rule, batch)).ToList();
            var text = (parts.Count > 1 ? $"Parte {i + 1} de {parts.Count} ({(level == BrokerLevel.Admin ? "pastas do sistema" : "sua conta")}).\n\n" : "") +
                       "Vai para a quarentena do NEXUS:\n" + string.Join("\n", part.Select(r => $"• {r.Rule.Title} — {(r.Measured ? Bytes(r.Bytes) : "não medido")}")) +
                       "\n\nNada é apagado de verdade: tudo pode voltar em \"Desfazer última limpeza\" ou na aba Quarentena. Arquivos em uso e programas abertos são pulados." +
                       (level == BrokerLevel.Admin ? "\n\nPede permissão de administrador (aviso do Windows) e cria um ponto de restauração antes." : "");
            var result = await host.ApplyAsync("Limpar selecionados", text, requests, level);
            if (result is null) { lines.Add(parts.Count > 1 ? $"Parte {i + 1}: cancelada." : "Cancelado. Nada foi alterado."); break; }
            lines.Add(Summarize(result, part));
        }
        Message = string.Join(" ", lines);
        await ScanAsync();
    }

    private static string Summarize(BrokerCallResult result, IReadOnlyList<CleaningRuleRow> part)
    {
        if (!result.Ok || result.Response?.Results is not { } items) return result.Message;
        var titles = part.Where(r => !r.Rule.IsUserRule).ToDictionary(r => r.Rule.ActionKey, r => r.Rule.Title, StringComparer.Ordinal);
        var ok = items.Count(x => x.Outcome is "Verified" or "AlreadyConfigured");
        var skipped = items.Where(x => x.Outcome is not ("Verified" or "AlreadyConfigured"))
            .Select(x => $"{titles.GetValueOrDefault(x.ActionKey, x.ActionKey == UserRuleActionKey ? "Sua regra" : x.ActionKey)}: {x.Detail}").ToList();
        return $"{ok} item(ns) limpo(s) e verificado(s)." + (skipped.Count > 0 ? " Pulados: " + string.Join("; ", skipped) + "." : "");
    }

    // ------------------------------------------------------------------ Lixeira (irreversível)

    private async Task MeasureRecycleBinAsync()
    {
        var options = await Task.Run(() =>
        {
            var list = new List<RecycleDriveOption>();
            if (RecycleBinInfo.TryQuery() is { } all) list.Add(new RecycleDriveOption("Todas as unidades", "all", all.Files, all.Bytes));
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady || d.Name.Length < 2 || !char.IsAsciiLetterUpper(char.ToUpperInvariant(d.Name[0]))) continue;
                    if (RecycleBinInfo.TryQuery(d.Name) is { } q)
                        list.Add(new RecycleDriveOption($"Unidade {char.ToUpperInvariant(d.Name[0])}:", char.ToUpperInvariant(d.Name[0]).ToString(), q.Files, q.Bytes));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return list;
        });
        var keep = _recycleDrive?.Drive;
        RecycleDrives.Clear();
        foreach (var o in options) RecycleDrives.Add(o);
        RecycleDrive = RecycleDrives.FirstOrDefault(o => o.Drive == keep) ?? RecycleDrives.FirstOrDefault();
        var total = options.FirstOrDefault(o => o.Drive == "all");
        RecycleText = total is null ? "Não foi possível ler a Lixeira." : total.Bytes == 0 ? "Lixeira vazia." : $"{Bytes(total.Bytes)} em {total.Items.ToString("N0", Pt)} item(ns).";
    }

    private async Task EmptyRecycleBinAsync()
    {
        var host = Host!;
        var choice = _recycleDrive;
        if (choice is null) return;
        // Tamanho exato agora (não o da última medição).
        var now = await Task.Run(() => RecycleBinInfo.TryQuery(choice.Drive == "all" ? null : choice.Drive + @":\"));
        if (now is not { } size) { Message = "Não foi possível ler a Lixeira. Nada foi alterado."; return; }
        if (size.Bytes == 0 && size.Files == 0) { Message = "A Lixeira já está vazia."; await MeasureRecycleBinAsync(); return; }
        var where = choice.Drive == "all" ? "de todas as unidades" : $"da unidade {choice.Drive}:";
        if (!host.Confirm("Esvaziar a Lixeira — não tem desfazer",
                $"Vai apagar DE VEZ o conteúdo da Lixeira {where}:\n\n• {size.Files.ToString("N0", Pt)} item(ns)\n• {Bytes(size.Bytes)} ({size.Bytes.ToString("N0", Pt)} bytes)\n\n" +
                "Não há quarentena nem desfazer. Se não tem certeza, abra a Lixeira e confira antes.\n\nContinuar?"))
        {
            Message = "Cancelado. Nada foi alterado.";
            return;
        }
        var request = new ActionRequest("cleaning.recycle-bin.empty@1", null, new Dictionary<string, string> { ["drive"] = choice.Drive }, ConfirmIrreversible: true);
        var result = await host.ApplyAsync("Esvaziar a Lixeira (irreversível)",
            $"Confirme de novo: apagar de vez {Bytes(size.Bytes)} da Lixeira {where}. Não há como desfazer.", [request], BrokerLevel.User);
        Message = result is null ? "Cancelado. Nada foi alterado." : IrreversibleSummary(result, "Lixeira esvaziada e conferida (0 itens).");
        await MeasureRecycleBinAsync();
        await LoadHistoryAsync();
    }

    private static string IrreversibleSummary(BrokerCallResult result, string ok)
    {
        if (!result.Ok || result.Response?.Results is not { } items) return result.Message;
        var bad = items.Where(x => x.Outcome is not ("Verified" or "AlreadyConfigured")).Select(x => x.Detail).Where(d => !string.IsNullOrEmpty(d)).ToList();
        return bad.Count == 0 ? ok : "Concluído com avisos: " + string.Join("; ", bad);
    }

    // ------------------------------------------------------------------ ferramentas oficiais do Windows

    private void OpenDiskCleanup()
    {
        if (Host is null || !Host.Confirm("Abrir a Limpeza de Disco do Windows",
                "Vai abrir a Limpeza de Disco (cleanmgr), a ferramenta do próprio Windows. Para ver \"Limpeza de atualizações do Windows\", " +
                "clique em \"Limpar arquivos do sistema\" (o Windows pede permissão de administrador). Você escolhe os itens e confirma lá.\n\n" +
                "O NEXUS não acompanha nem desfaz o que a Limpeza de Disco apagar. Abrir?"))
            return;
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cleanmgr.exe");
        if (!File.Exists(exe)) { Message = "A Limpeza de Disco não está instalada neste Windows. Use Configurações > Armazenamento."; return; }
        try { using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = "Não foi possível abrir: " + ex.Message; }
    }

    private void TryOpen(string tool)
    {
        try { OpenNativeTool(tool); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = "Não foi possível abrir: " + ex.Message; }
    }

    internal static void OpenNativeTool(string tool)
    {
        // Só ferramentas do próprio Windows, que o usuário vê e controla (validado nas regras: ms-settings: ou shell:).
        if (!tool.StartsWith("shell:", StringComparison.Ordinal) && !tool.StartsWith("ms-settings:", StringComparison.Ordinal)) return;
        var psi = tool.StartsWith("shell:", StringComparison.Ordinal)
            ? new ProcessStartInfo("explorer.exe", tool) { UseShellExecute = true }
            : new ProcessStartInfo(tool) { UseShellExecute = true };
        using var _ = Process.Start(psi);
    }

    // ------------------------------------------------------------------ quarentena, histórico e desfazer

    private sealed record HistoryData(CleaningHistoryReport Report, List<(QuarantineBatch Batch, BrokerLevel Level, CleaningSession Session)> Parts);

    private async Task LoadHistoryAsync()
    {
        var data = await Task.Run(ReadHistory);
        _report = data.Report;
        Batches.Clear();
        foreach (var row in data.Parts.GroupBy(p => p.Batch.BatchId).Select(g => new QuarantineBatchRow(this, g.Key, g.ToList())).OrderByDescending(r => r.When))
            Batches.Add(row);
        Sessions.Clear();
        foreach (var s in data.Report.Sessions.Take(200)) Sessions.Add(new HistorySessionRow(s));
        Months.Clear();
        var max = Math.Max(1, data.Report.Months.Max(m => Math.Max(m.Cleaned, m.Recovered)));
        foreach (var m in data.Report.Months) Months.Add(new MonthBar(m, max));

        var inQuarantine = data.Report.Recoverable;
        QuarantineText = inQuarantine == 0 ? "Quarentena vazia." : $"Na quarentena: {Bytes(inQuarantine)} em {Batches.Count(r => r.BytesInQuarantine > 0)} limpeza(s).";
        HistoryEmptyText = data.Report.Sessions.Count == 0 && data.Report.RecycleBin.Count == 0 ? "Nenhuma limpeza ainda." : "";
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasBatches));
        OnPropertyChanged(nameof(RecoveredMonthBytes));
        OnPropertyChanged(nameof(RecoveredTotalBytes));
        OnPropertyChanged(nameof(RecoverableQuarantineBytes));
        OnPropertyChanged(nameof(CleanedMonthBytes));
        OnPropertyChanged(nameof(RecoveredMonthText));
        OnPropertyChanged(nameof(RecoveredTotalText));
        OnPropertyChanged(nameof(RecoverableQuarantineText));
        OnPropertyChanged(nameof(CleanedMonthText));
        OnPropertyChanged(nameof(RestoredTotalText));
        UpdateExpirySuggestion();
    }

    private static HistoryData ReadHistory()
    {
        var batches = new List<(QuarantineBatch, CleaningLevel)>();
        var journals = new List<(IJournal, CleaningLevel)>();
        var opened = new List<IDisposable>();
        try
        {
            foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
            {
                var cl = level == BrokerLevel.User ? CleaningLevel.User : CleaningLevel.Admin;
                try { batches.AddRange(QuarantineStore.ReadBatches(DataDirectory(level)).Select(b => (b, cl))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                var path = LocalBroker.JournalPath(level);
                if (!File.Exists(path)) continue;
                try
                {
                    var j = SqliteJournal.OpenReadOnly(path);
                    opened.Add(j);
                    journals.Add((j, cl));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { }
            }
            CleaningHistoryReport report;
            try { report = CleaningHistory.Build(batches, journals, DateTime.UtcNow); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or JsonException)
            {
                report = CleaningHistory.Build(batches, [], DateTime.UtcNow); // diário ilegível: só os manifestos
            }
            var parts = new List<(QuarantineBatch, BrokerLevel, CleaningSession)>();
            foreach (var (b, cl) in batches)
                if (report.Sessions.FirstOrDefault(s => s.BatchId == b.BatchId && s.Level == cl) is { } s)
                    parts.Add((b, cl == CleaningLevel.User ? BrokerLevel.User : BrokerLevel.Admin, s));
            return new HistoryData(report, parts);
        }
        finally
        {
            foreach (var d in opened) d.Dispose();
        }
    }

    internal Task RestoreBatchAsync(QuarantineBatchRow row) => RunAsync(() => RestoreAsync(row));
    internal Task PurgeBatchAsync(QuarantineBatchRow row) => RunAsync(() => PurgeAsync([row], "este lote"));

    private async Task RestoreAsync(QuarantineBatchRow? row)
    {
        if (row is null || Host is null) return;
        var results = new List<string>();
        foreach (var (level, run) in row.RevertibleRuns)
        {
            var r = await Host.RevertAsync(run, level);
            if (r is null) { results.Add("Cancelado."); break; }
            results.Add(r.Response?.FullyRestored == true ? "Arquivos devolvidos e conferidos." : r.Message);
        }
        Message = string.Join(" ", results.Distinct(StringComparer.Ordinal));
        await ScanAsync();
    }

    private async Task PurgeOlderAsync(int days)
    {
        if (days < 1) return;
        var limit = DateTime.UtcNow.AddDays(-days);
        var old = Batches.Where(b => b.CanPurge && b.When <= limit).ToList();
        if (old.Count == 0) { Message = $"Nenhuma limpeza com mais de {days} dias na quarentena."; return; }
        await PurgeAsync(old, $"as limpezas com mais de {days} dias");
    }

    /// <summary>
    /// Esvazia lotes (irreversível). Mede o tamanho EXATO no disco agora, mostra a lista, pede a primeira confirmação aqui e a
    /// segunda no Host.ApplyAsync; o pedido leva ConfirmIrreversible. Um lote por ação (lista exata), no máximo 64 por pedido.
    /// </summary>
    private async Task PurgeAsync(IReadOnlyList<QuarantineBatchRow> rows, string what)
    {
        var host = Host!;
        var items = await Task.Run(() => rows.SelectMany(r => r.Parts.Select(p => (Row: r, p.Level, p.Batch.BatchId, Size: QuarantineStore.MeasureOnDisk(DataDirectory(p.Level), p.Batch.BatchId))))
            .Where(x => x.Size.Files > 0 || x.Size.Bytes > 0).ToList());
        if (items.Count == 0) { Message = "Nada a esvaziar: a quarentena destes lotes já está vazia."; await LoadHistoryAsync(); return; }
        var totalBytes = items.Sum(x => x.Size.Bytes);
        var totalFiles = items.Sum(x => x.Size.Files);
        var list = items.GroupBy(x => x.Row).Take(15).Select(g => $"• {g.Key.WhenText} — {g.Key.RulesText} — {Bytes(g.Sum(x => x.Size.Bytes))}").ToList();
        var more = items.Select(x => x.Row).Distinct().Count() - list.Count;
        if (!host.Confirm("Esvaziar a quarentena — não tem desfazer",
                $"Vai apagar DE VEZ {what}:\n\n{string.Join("\n", list)}{(more > 0 ? $"\n• e mais {more} limpeza(s)" : "")}\n\n" +
                $"Total: {Bytes(totalBytes)} ({totalBytes.ToString("N0", Pt)} bytes) em {totalFiles.ToString("N0", Pt)} arquivo(s).\n\n" +
                "Depois disso, estas limpezas não podem mais ser desfeitas. O espaço volta para o disco." +
                (items.Any(x => x.Level == BrokerLevel.Admin) ? "\n\nParte é do administrador: o Windows vai pedir permissão." : "") + "\n\nContinuar?"))
        {
            Message = "Cancelado. Nada foi alterado.";
            return;
        }
        var parts = items.GroupBy(x => x.Level).OrderBy(g => g.Key).SelectMany(g => g.Chunk(MaxPerRequest).Select(c => (Level: g.Key, Items: c.ToList()))).ToList();
        var lines = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var (level, chunk) = parts[i];
            var id = level == BrokerLevel.Admin ? "cleaning.quarantine.purge.machine@1" : "cleaning.quarantine.purge@1";
            var requests = chunk.Select(x => new ActionRequest(id, null, new Dictionary<string, string> { ["batch"] = x.BatchId }, ConfirmIrreversible: true)).ToList();
            var text = (parts.Count > 1 ? $"Parte {i + 1} de {parts.Count}. " : "") +
                       $"Confirme de novo: apagar de vez {Bytes(chunk.Sum(x => x.Size.Bytes))} ({chunk.Count} lote(s), {(level == BrokerLevel.Admin ? "administrador" : "sua conta")}). Não há como desfazer.";
            var result = await host.ApplyAsync("Esvaziar a quarentena (irreversível)", text, requests, level);
            if (result is null) { lines.Add(parts.Count > 1 ? $"Parte {i + 1}: cancelada." : "Cancelado. Nada foi alterado."); break; }
            lines.Add(IrreversibleSummary(result, $"{Bytes(chunk.Sum(x => x.Size.Bytes))} apagados de vez e conferidos (quarentena desses lotes = 0)."));
        }
        Message = string.Join(" ", lines);
        await LoadHistoryAsync();
    }

    private void UpdateExpirySuggestion()
    {
        var days = _settings.QuarantineExpiryDays;
        if (days <= 0) { ExpirySuggestion = ""; return; }
        var limit = DateTime.UtcNow.AddDays(-days);
        var old = Batches.Where(b => b.CanPurge && b.When <= limit).ToList();
        ExpirySuggestion = old.Count == 0 ? "" :
            $"{old.Count} limpeza(s) com mais de {days} dias ocupam {Bytes(old.Sum(b => b.BytesInQuarantine))} na quarentena. Quer esvaziar? (pede duas confirmações)";
    }

    private void ExportCsv()
    {
        if (_report is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar histórico de limpezas",
            FileName = $"nexus-limpezas-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv",
            Filter = "CSV (*.csv)|*.csv",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, CleaningHistory.ToCsv(_report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Message = "Histórico exportado: " + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Message = "Não foi possível salvar: " + ex.Message; }
    }

    // ------------------------------------------------------------------ regras do usuário

    private void ApplyUserRules(UserRuleLoad load)
    {
        _userRules = load;
        RuleFiles.Clear();
        foreach (var f in load.Files) RuleFiles.Add(new UserRuleFileRow(f, load.Rules));
        var errors = load.Files.Count(f => f.Error is not null);
        RulesSummary = load.Files.Count == 0 ? "Nenhum arquivo de regras ainda. Clique em \"Abrir pasta de regras\": o NEXUS cria a pasta com um exemplo desativado."
            : $"{load.Rules.Rules.Count} regra(s) ativa(s) em {load.Files.Count(f => f.Error is null && f.Enabled)} arquivo(s)" +
              (errors > 0 ? $"; {errors} arquivo(s) com erro (não valem até corrigir)." : ".");
        RulesFolderProblem = load.FolderProblem ?? "";
    }

    private async Task ReloadRulesAsync()
    {
        ApplyUserRules(await Task.Run(() => UserCleaningRules.Load(UserRulesFolder)));
        Message = "Regras recarregadas. " + RulesSummary;
        await ScanAsync();
    }

    private void OpenRulesFolder()
    {
        var folder = UserRulesFolder;
        if (folder is null) { Message = "Pasta de dados do Windows (%AppData%) indisponível."; return; }
        try
        {
            // Preferências do próprio NEXUS (não é estado do sistema): a pasta e, na primeira vez, um exemplo DESATIVADO.
            Directory.CreateDirectory(folder);
            if (!_settings.RulesExampleCopied)
            {
                var example = Path.Combine(folder, UserCleaningRules.ExampleFileName);
                if (!File.Exists(example)) File.WriteAllText(example, UserCleaningRules.ExampleJson, new UTF8Encoding(false));
                _settings = _settings with { RulesExampleCopied = true };
                _settings.Save();
            }
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = "Não foi possível abrir a pasta: " + ex.Message;
        }
        _ = ReloadRulesAsync();
    }
}

/// <summary>Preferências locais da página Limpeza (%LocalAppData%\NEXUS\cleaning-settings.json). Não é estado do sistema.</summary>
/// <param name="QuarantineExpiryDays">0 = desligado (padrão). Ligado, só SUGERE esvaziar lotes mais velhos (nunca apaga sozinho).</param>
public sealed record CleaningSettings(int QuarantineExpiryDays, bool RulesExampleCopied)
{
    private static string PathOf => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "cleaning-settings.json");

    public static CleaningSettings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<CleaningSettings>(File.ReadAllText(PathOf)) ?? new(0, false);
            return s.QuarantineExpiryDays is < 0 or > 999 ? s with { QuarantineExpiryDays = 0 } : s;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return new(0, false); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Opção de lista (rótulo + número).</summary>
public sealed record CleaningOption(string Label, int Value)
{
    public override string ToString() => Label;
}

/// <summary>Unidade para esvaziar a Lixeira ("all" = todas).</summary>
public sealed record RecycleDriveOption(string Name, string Drive, long Items, long Bytes)
{
    public string Label => $"{Name} — {(Bytes == 0 ? "vazia" : CleaningPage.Bytes(Bytes))}";
    public override string ToString() => Label;
}

/// <summary>Uma categoria (Sistema, Navegadores, Apps, Jogos, Desenvolvedor, Suas regras) com caixa "marcar todos" e total.</summary>
public sealed class CleaningCategoryGroup : ObservableObject
{
    private readonly CleaningPage _page;
    private string _notFound = "";

    internal CleaningCategoryGroup(CleaningPage page, string name)
    {
        _page = page;
        Name = name;
    }

    public string Name { get; }
    public ObservableCollection<CleaningRuleRow> Rows { get; } = [];

    /// <summary>Ícone da categoria (Themes/Icons.xaml), para o cartão.</summary>
    public string IconKey => Name switch
    {
        "Sistema" => "Icon.Windows",
        "Navegadores" => "Icon.Browser",
        "Apps" => "Icon.Apps",
        "Jogos" => "Icon.Games",
        "Suas regras" => "Icon.User",
        _ => "Icon.Code",
    };

    public string CountText => Rows.Count == 1 ? "1 item" : $"{Rows.Count} itens";
    public string NotFoundText { get => _notFound; set { if (Set(ref _notFound, value)) OnPropertyChanged(nameof(HasNotFound)); } }
    public bool HasNotFound => _notFound.Length > 0;
    public string TotalText => CleaningPage.Bytes(Rows.Where(r => r.CanClean || r.Rule.Mode == CleaningMode.MeasureOnly).Sum(r => r.Bytes));

    public bool? AllSelected
    {
        get
        {
            var rows = Rows.Where(r => r.CanClean).ToList();
            if (rows.Count == 0 || rows.All(r => !r.IsSelected)) return false;
            return rows.All(r => r.IsSelected) ? true : null;
        }
        set
        {
            // Marcar todos nunca marca itens sensíveis (histórico, cookies): esses são sempre escolha individual.
            var on = value == true;
            foreach (var r in Rows.Where(r => r.CanClean && (!on || !r.Rule.Sensitive))) r.SetSelected(on);
            _page.SelectionChanged();
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(TotalText));
    }
}

/// <summary>Uma regra medida.</summary>
public sealed class CleaningRuleRow : ObservableObject
{
    private readonly CleaningPage _page;
    private bool _selected;

    internal CleaningRuleRow(CleaningPage page, RuleScanResult scan)
    {
        _page = page;
        Rule = scan.Rule;
        Bytes = scan.Bytes;
        Measured = scan.Measured;
        RunningApps = scan.RunningApps;
        Scan = scan;
        OpenToolCommand = new RelayCommand(() =>
        {
            try { CleaningPage.OpenNativeTool(Rule.NativeTool!); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }, () => Rule.NativeTool is not null);
    }

    public CleaningRule Rule { get; }
    public RuleScanResult Scan { get; }
    public long Bytes { get; }
    public bool Measured { get; }
    public IReadOnlyList<string> RunningApps { get; }
    public RelayCommand OpenToolCommand { get; }

    public bool CanClean => Rule.Mode == CleaningMode.Quarantine;
    public bool IsMeasureOnly => Rule.Mode == CleaningMode.MeasureOnly;
    public string Title => Rule.Title;
    public string Explanation => Rule.Explanation;
    public string ExpectedGain => Rule.ExpectedGain;
    public string? Warning => Rule.Warning;
    public bool HasWarning => !string.IsNullOrEmpty(Rule.Warning);
    public string SizeText => Measured ? CleaningPage.Bytes(Bytes) : "—";
    public string FilesText => !Measured ? Scan.Problem ?? "" : Scan.Files == 0 ? "nada a limpar" : $"{Scan.Files.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} arquivo(s)";

    public string Tags
    {
        get
        {
            var t = new List<string>();
            if (Rule.IsUserRule) t.Add("sua regra");
            if (Rule.Category == CleaningCategory.Desenvolvedor) t.Add("desenvolvedor");
            if (Rule.Admin) t.Add("administrador");
            if (Rule.MinAgeHours >= 24) t.Add($"só com mais de {Rule.MinAgeHours / 24:0.#} dia(s)");
            if (RunningApps.Count > 0) t.Add($"aberto: {string.Join(", ", RunningApps)} (será pulado)");
            if (IsMeasureOnly) t.Add("feito pelo Windows");
            return string.Join(" · ", t);
        }
    }

    public string ToolLabel => Rule.Special == "RecycleBin" ? "Abrir a Lixeira" : "Abrir no Windows";

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (!CanClean) return;
            if (Set(ref _selected, value)) _page.SelectionChanged();
        }
    }

    internal void SetSelected(bool value)
    {
        if (CanClean) Set(ref _selected, value, nameof(IsSelected));
    }
}

/// <summary>Uma limpeza na quarentena (um lote; pode ter parte da conta do usuário e parte de administrador).</summary>
public sealed class QuarantineBatchRow
{
    private readonly CleaningPage _page;

    internal QuarantineBatchRow(CleaningPage page, string batchId, IReadOnlyList<(QuarantineBatch Batch, BrokerLevel Level, CleaningSession Session)> parts)
    {
        _page = page;
        BatchId = batchId;
        Parts = parts;
        When = parts.Min(p => p.Batch.CreatedUtc);
        RestoreCommand = new RelayCommand(() => _ = _page.RestoreBatchAsync(this), () => CanRestore && !_page.Busy);
        PurgeCommand = new RelayCommand(() => _ = _page.PurgeBatchAsync(this), () => CanPurge && !_page.Busy);
    }

    public string BatchId { get; }
    public IReadOnlyList<(QuarantineBatch Batch, BrokerLevel Level, CleaningSession Session)> Parts { get; }
    public DateTime When { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand PurgeCommand { get; }

    public long BytesInQuarantine => Parts.Sum(p => p.Batch.BytesInQuarantine);
    public bool CanPurge => Parts.Any(p => p.Batch.BytesInQuarantine > 0 || p.Batch.FilesInQuarantine > 0);

    /// <summary>Execuções do diário que ainda têm algo na quarentena (o desfazer é o RevertRun delas).</summary>
    public IReadOnlyList<(BrokerLevel Level, Guid Run)> RevertibleRuns =>
        Parts.Where(p => p.Batch.BytesInQuarantine > 0 && !p.Batch.AnyPurged && p.Session.RunId is not null).Select(p => (p.Level, p.Session.RunId!.Value)).ToList();

    public bool CanRestore => RevertibleRuns.Count > 0;
    public string WhenText => When.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
    public string RulesText => string.Join(", ", Parts.SelectMany(p => p.Session.Rules).Distinct(StringComparer.Ordinal).DefaultIfEmpty("nada foi movido (tudo em uso ou vazio)"));
    public string SizeText => CleaningPage.Bytes(BytesInQuarantine);
    public string LevelText => string.Join(" + ", Parts.Select(p => p.Level == BrokerLevel.Admin ? "administrador" : "sua conta").Distinct(StringComparer.Ordinal));
    public string StatusText
    {
        get
        {
            var statuses = Parts.Select(p => p.Session.Status).Distinct(StringComparer.Ordinal).ToList();
            var s = string.Join("; ", statuses);
            if (BytesInQuarantine > 0 && !CanRestore) s += " (sem registro no diário para desfazer)";
            return s + " · " + LevelText;
        }
    }
}

/// <summary>Uma linha do histórico.</summary>
public sealed class HistorySessionRow(CleaningSession session)
{
    public CleaningSession Session { get; } = session;
    public string WhenText => Session.StartedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
    public string SizeText => CleaningPage.Bytes(Session.Bytes);
    public string RulesText => Session.Rules.Count == 0 ? "nada foi movido" : string.Join(", ", Session.Rules);
    public string DetailText
    {
        get
        {
            var parts = new List<string> { $"{Session.Files.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} arquivo(s)", Session.Level == CleaningLevel.Admin ? "administrador" : "sua conta", Session.Status };
            if (Session.BytesPurged > 0) parts.Add($"{CleaningPage.Bytes(Session.BytesPurged)} recuperados de verdade");
            if (Session.BytesRestored > 0) parts.Add($"{CleaningPage.Bytes(Session.BytesRestored)} devolvidos");
            if (Session.UserRules) parts.Add("inclui suas regras");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Barra de um mês no gráfico do histórico (alturas já em pixels).</summary>
public sealed class MonthBar(CleaningMonth month, long max)
{
    private const double Height = 90;
    public string Label => month.Label;
    public double CleanedHeight => Math.Max(month.Cleaned > 0 ? 2 : 0, Height * month.Cleaned / max);
    public double RecoveredHeight => Math.Max(month.Recovered > 0 ? 2 : 0, Height * month.Recovered / max);
    public string Tooltip => $"{month.Label}: limpo {CleaningPage.Bytes(month.Cleaned)} · recuperado de verdade {CleaningPage.Bytes(month.Recovered)}";
}

/// <summary>Um arquivo de regras do usuário (com erro, desativado ou com as regras que valem).</summary>
public sealed class UserRuleFileRow(UserRuleFile file, CleaningRuleSet loaded)
{
    public string Name => file.Name;
    public bool HasError => file.Error is not null;
    public string StatusText => file.Error is not null ? "Erro: " + file.Error
        : !file.Enabled ? "Desativado (\"enabled\": false)"
        : file.RuleIds.Count == 0 ? "Sem regras"
        : string.Join(", ", file.RuleIds.Select(id => loaded.Find(id)?.Title ?? id));
}
