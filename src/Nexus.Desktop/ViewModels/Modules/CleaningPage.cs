using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions.Registry;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Limpeza: mede (somente leitura, aqui mesmo) o que cada regra recupera e pede a limpeza ao aplicador. Tudo o que sai vai para a
/// quarentena do NEXUS (Host.ApplyAsync), e cada lote pode voltar (Host.RevertAsync). A página nunca move nem apaga arquivos.
/// </summary>
public sealed class CleaningPage : ModulePage
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private CancellationTokenSource? _scanCancel;
    private bool _scanning;
    private bool _scanned;
    private string _recoverableText = "—";
    private string _recoverableCaption = "Medindo o que pode ser liberado…";
    private string _selectedText = "";
    private string _quarantineText = "Quarentena vazia.";
    private string _openApps = "";
    private string _historyEmptyText = "Nenhuma limpeza ainda.";

    public CleaningPage()
    {
        RescanCommand = new RelayCommand(() => _ = ScanAsync(), () => !_scanning && !Busy);
        CancelScanCommand = new RelayCommand(() => _scanCancel?.Cancel(), () => _scanning);
        CleanCommand = new RelayCommand(() => _ = RunAsync(CleanSelectedAsync), () => !Busy && !_scanning && Host is not null && SelectedRows().Any());
        UndoLastCommand = new RelayCommand(() => _ = RunAsync(() => RestoreAsync(History.FirstOrDefault(h => h.CanRestore))), () => !Busy && History.Any(h => h.CanRestore));
    }

    public override string Title => "Limpeza";
    public override string Group => "Limpar";
    public override string Subtitle => "Libere espaço com segurança: o que sai vai para uma quarentena e pode voltar.";

    public ObservableCollection<CleaningCategoryGroup> Categories { get; } = [];
    public ObservableCollection<CleaningBatchRow> History { get; } = [];

    public RelayCommand RescanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand CleanCommand { get; }
    public RelayCommand UndoLastCommand { get; }

    public bool Scanning { get => _scanning; private set => Set(ref _scanning, value); }
    public string RecoverableText { get => _recoverableText; private set => Set(ref _recoverableText, value); }
    public string RecoverableCaption { get => _recoverableCaption; private set => Set(ref _recoverableCaption, value); }
    public string SelectedText { get => _selectedText; private set => Set(ref _selectedText, value); }
    public string QuarantineText { get => _quarantineText; private set => Set(ref _quarantineText, value); }
    public string OpenAppsWarning { get => _openApps; private set { if (Set(ref _openApps, value)) OnPropertyChanged(nameof(HasOpenApps)); } }
    public bool HasOpenApps => OpenAppsWarning.Length > 0;
    public string HistoryEmptyText { get => _historyEmptyText; private set => Set(ref _historyEmptyText, value); }
    public bool HasHistory => History.Count > 0;

    public string QuarantineNote { get; } =
        "Os arquivos limpos ficam na quarentena, no mesmo disco, até ela ser esvaziada: só então o espaço volta de fato. " +
        "Esvaziar a quarentena (apagar de vez, sem volta) chega numa próxima versão, com confirmação própria.";

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

    private IEnumerable<CleaningRuleRow> AllRows() => Categories.SelectMany(c => c.Rows);
    private IEnumerable<CleaningRuleRow> SelectedRows() => AllRows().Where(r => r.IsSelected && r.CanClean);

    internal void SelectionChanged()
    {
        var selected = SelectedRows().ToList();
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
            var rules = CleaningRuleSet.BuiltIn.Rules;
            var folders = WindowsCleaningFolders.ForCurrentProcess(new WindowsRegistryBackend()) with
            {
                ExtraForbidden = [Path.Combine(UserDataDirectory, "quarantine"), Path.Combine(AdminDataDirectory, "quarantine")],
            };
            var results = await Task.Run(() => CleaningScanner.ScanAsync(rules, folders, SystemProcessProbe.Instance, DateTime.UtcNow,
                RecycleBinInfo.TryQuery, cancel: cancel.Token), cancel.Token);
            Populate(results);
            _scanned = true;
            await LoadHistoryAsync();
        }
        catch (OperationCanceledException) { RecoverableCaption = "Medição cancelada."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { RecoverableCaption = $"Não foi possível medir: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_scanCancel, cancel)) _scanCancel = null;
            Scanning = false;
        }
    }

    private void Populate(IReadOnlyList<RuleScanResult> results)
    {
        var previous = AllRows().ToDictionary(r => r.Rule.Id, r => r.IsSelected, StringComparer.Ordinal);
        Categories.Clear();
        foreach (var group in results.GroupBy(r => r.Rule.Category).OrderBy(g => g.Key))
        {
            var cat = new CleaningCategoryGroup(this, group.Key switch
            {
                CleaningCategory.Sistema => "Sistema",
                CleaningCategory.Navegadores => "Navegadores",
                CleaningCategory.Apps => "Apps",
                _ => "Jogos",
            });
            var missing = new List<string>();
            foreach (var r in group)
            {
                // Programa não instalado (nenhuma pasta encontrada e nada a medir): só aparece na lista de ausentes.
                if (r.Rule.Special is null && r.RootsFound == 0 && r.Problem is null) { missing.Add(r.Rule.Title.Split(':')[0]); continue; }
                var row = new CleaningRuleRow(this, r);
                row.SetSelected(previous.TryGetValue(r.Rule.Id, out var was) ? was : r.Rule.DefaultSelected && row.CanClean && (r.Bytes > 0 || !r.Measured));
                cat.Rows.Add(row);
            }
            cat.NotFoundText = missing.Count == 0 ? "" : "Não encontrados neste PC: " + string.Join(", ", missing.Distinct(StringComparer.CurrentCulture)) + ".";
            if (cat.Rows.Count > 0 || missing.Count > 0) Categories.Add(cat);
        }
        var cleanable = AllRows().Where(r => r.CanClean).ToList();
        var total = cleanable.Sum(r => r.Bytes);
        RecoverableText = Bytes(total);
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
        var parts = selected.GroupBy(r => r.Rule.Admin).OrderBy(g => g.Key).ToList();
        var lines = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i].ToList();
            var level = parts[i].Key ? BrokerLevel.Admin : BrokerLevel.User;
            var requests = part.Select(r => new ActionRequest(r.Rule.ActionKey, null, new Dictionary<string, string> { ["batch"] = batch })).ToList();
            var text = (parts.Count > 1 ? $"Parte {i + 1} de {parts.Count} ({(level == BrokerLevel.Admin ? "pastas do sistema" : "sua conta")}).\n\n" : "") +
                       "Vai para a quarentena do NEXUS:\n" + string.Join("\n", part.Select(r => $"• {r.Rule.Title} — {(r.Measured ? Bytes(r.Bytes) : "não medido")}")) +
                       "\n\nNada é apagado de verdade: tudo pode voltar em \"Desfazer última limpeza\". Arquivos em uso e programas abertos são pulados." +
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
        var title = part.ToDictionary(r => r.Rule.ActionKey, r => r.Rule.Title, StringComparer.Ordinal);
        var ok = items.Count(x => x.Outcome is "Verified" or "AlreadyConfigured");
        var skipped = items.Where(x => x.Outcome is not ("Verified" or "AlreadyConfigured"))
            .Select(x => $"{title.GetValueOrDefault(x.ActionKey, x.ActionKey)}: {x.Detail}").ToList();
        return $"{ok} item(ns) limpo(s) e verificado(s)." + (skipped.Count > 0 ? " Pulados: " + string.Join("; ", skipped) + "." : "");
    }

    // ------------------------------------------------------------------ histórico e desfazer (lotes da quarentena + diário)

    private async Task LoadHistoryAsync()
    {
        var rows = await Task.Run(ReadHistory);
        History.Clear();
        foreach (var r in rows) History.Add(r);
        var inQuarantine = rows.Sum(r => r.BytesInQuarantine);
        QuarantineText = inQuarantine == 0 ? "Quarentena vazia." : $"Na quarentena: {Bytes(inQuarantine)} em {rows.Count(r => r.BytesInQuarantine > 0)} limpeza(s).";
        HistoryEmptyText = rows.Count == 0 ? "Nenhuma limpeza ainda." : "";
        OnPropertyChanged(nameof(HasHistory));
    }

    private List<CleaningBatchRow> ReadHistory()
    {
        var parts = new List<(QuarantineBatch Batch, BrokerLevel Level, Guid? Run)>();
        foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
        {
            var dataDir = level == BrokerLevel.User ? UserDataDirectory : AdminDataDirectory;
            IReadOnlyList<QuarantineBatch> batches;
            try { batches = QuarantineStore.ReadBatches(dataDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (batches.Count == 0) continue;
            var runs = RunsByBatch(LocalBroker.JournalPath(level));
            parts.AddRange(batches.Select(b => (b, level, runs.TryGetValue(b.BatchId, out var run) ? run : (Guid?)null)));
        }
        return parts.GroupBy(p => p.Batch.BatchId).Select(g => new CleaningBatchRow(this, g.Key, g.ToList()))
            .OrderByDescending(r => r.When).Take(30).ToList();
    }

    /// <summary>Execução do diário (somente leitura) que limpou cada lote.</summary>
    private static Dictionary<string, Guid> RunsByBatch(string journalPath)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (!File.Exists(journalPath)) return map;
        try
        {
            using var journal = SqliteJournal.OpenReadOnly(journalPath);
            foreach (var run in journal.Runs())
                foreach (var e in journal.Entries(run.RunId))
                    if (e.Action.Definition.HandlerKind == CleaningRuleSet.HandlerKind && e.Action.Target.TryGetValue("batch", out var b))
                        map[b] = run.RunId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { }
        return map;
    }

    internal Task RestoreBatchAsync(CleaningBatchRow row) => RunAsync(() => RestoreAsync(row));

    private async Task RestoreAsync(CleaningBatchRow? row)
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

    internal static void OpenNativeTool(string tool)
    {
        // Só ferramentas do próprio Windows, que o usuário vê e controla (validado nas regras: ms-settings: ou shell:).
        var psi = tool.StartsWith("shell:", StringComparison.Ordinal)
            ? new ProcessStartInfo("explorer.exe", tool) { UseShellExecute = true }
            : new ProcessStartInfo(tool) { UseShellExecute = true };
        using var _ = Process.Start(psi);
    }
}

/// <summary>Uma categoria (Sistema, Navegadores, Apps, Jogos) com caixa "marcar todos" e total.</summary>
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

/// <summary>Uma limpeza do histórico (um lote; pode ter parte da conta do usuário e parte de administrador).</summary>
public sealed class CleaningBatchRow
{
    private readonly CleaningPage _page;

    internal CleaningBatchRow(CleaningPage page, string batchId, IReadOnlyList<(QuarantineBatch Batch, BrokerLevel Level, Guid? Run)> parts)
    {
        _page = page;
        BatchId = batchId;
        Parts = parts;
        When = parts.Min(p => p.Batch.CreatedUtc);
        RestoreCommand = new RelayCommand(() => _ = _page.RestoreBatchAsync(this), () => CanRestore && !_page.Busy);
    }

    public string BatchId { get; }
    public IReadOnlyList<(QuarantineBatch Batch, BrokerLevel Level, Guid? Run)> Parts { get; }
    public DateTime When { get; }
    public RelayCommand RestoreCommand { get; }

    public long BytesInQuarantine => Parts.Sum(p => p.Batch.BytesInQuarantine);
    public bool FullyRestored => Parts.All(p => p.Batch.FullyRestored);

    /// <summary>Execuções do diário que ainda têm algo na quarentena (o desfazer é o RevertRun delas).</summary>
    public IReadOnlyList<(BrokerLevel Level, Guid Run)> RevertibleRuns =>
        Parts.Where(p => !p.Batch.FullyRestored && p.Run is not null).Select(p => (p.Level, p.Run!.Value)).ToList();

    public bool CanRestore => RevertibleRuns.Count > 0;
    public string WhenText => When.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
    public string RulesText => string.Join(", ", Parts.SelectMany(p => p.Batch.Rules).Where(r => r.MovedFiles > 0 || r.Status == QuarantineManifest.Restored)
        .Select(r => r.RuleTitle).DefaultIfEmpty("nada foi movido (tudo em uso ou vazio)"));
    public string SizeText => CleaningPage.Bytes(Parts.Sum(p => p.Batch.Rules.Sum(r => r.MovedBytes)));
    public string StatusText => FullyRestored ? "restaurado" : CanRestore ? "na quarentena" : "na quarentena (sem registro no diário para desfazer)";
}
