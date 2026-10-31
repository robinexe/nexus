using System.Collections.ObjectModel;
using Nexus.Boot.Impact;
using Nexus.Boot.Inventory;
using Nexus.Boot.Windows;
using Nexus.Broker;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Inicialização: o que abre com o Windows (Run/RunOnce, pastas Inicializar, tarefas de logon/boot, serviços automáticos de
/// terceiros), o tempo de boot medido pelo próprio Windows e o impacto de cada item quando há medição. Só lê; ligar/desligar e
/// remover entradas órfãs vão pelo aplicador (Host.ApplyAsync), com diário e Desfazer.
/// </summary>
public sealed class StartupPage : ModulePage
{
    private bool _loaded;
    private string _heroValue = "…";
    private string _heroDetail = "Lendo o histórico de inicialização do Windows…";
    private string _summary = "";
    private string _search = "";
    private bool _showWindows;
    private bool _hasBootData;
    private Guid? _lastRun;
    private Nexus.Broker.BrokerLevel _lastLevel;
    private string _lastChange = "";
    private List<StartupRow> _all = [];
    private IReadOnlyList<StartupRow> _visible = [];

    public StartupPage()
    {
        RefreshCommand = new RelayCommand(() => _ = RunAsync(LoadAsync), () => !Busy);
        UndoCommand = new RelayCommand(() => _ = RunAsync(UndoAsync), () => !Busy && _lastRun is not null);
    }

    public override string Title => "Inicialização";
    public override string Group => "Sistema";
    public override string Subtitle => "O que abre junto com o Windows e quanto pesa.";

    public string HeroValue { get => _heroValue; private set => Set(ref _heroValue, value); }
    public string HeroDetail { get => _heroDetail; private set => Set(ref _heroDetail, value); }
    public bool HasBootData { get => _hasBootData; private set => Set(ref _hasBootData, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public ObservableCollection<BootBar> BootBars { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public IReadOnlyList<StartupRow> Visible { get => _visible; private set => Set(ref _visible, value); }

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) ApplyFilter(); }
    }

    /// <summary>Mostrar também itens do próprio Windows (Microsoft), que normalmente não se desliga.</summary>
    public bool ShowWindowsItems
    {
        get => _showWindows;
        set { if (Set(ref _showWindows, value)) ApplyFilter(); }
    }

    public string LastChange { get => _lastChange; private set { if (Set(ref _lastChange, value)) OnPropertyChanged(nameof(CanUndo)); } }
    public bool CanUndo => _lastRun is not null;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand UndoCommand { get; }

    public override Task OnShownAsync() => _loaded ? Task.CompletedTask : RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        Message = "";
        var (inventory, report) = await Task.Run(() =>
        {
            var r = WindowsBootEventReader.Read();
            var inv = new StartupInventory(new WindowsBootRegistryReader(), new WindowsFileInspector(), Environment.GetEnvironmentVariable,
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                .Read(WindowsStartupFolders.Current(), new WindowsTaskScheduler(), new WindowsServiceReader(), r);
            return (inv, r);
        });
        _loaded = true;
        ShowBoot(report);
        _all = inventory.Items.Select(i => new StartupRow(i, this)).ToList();
        Notes.Clear();
        foreach (var n in inventory.Notes) Notes.Add(n);
        var visible = _all.Where(r => !r.Item.IsMicrosoft).ToList();
        Summary = $"{visible.Count} itens de terceiros · {visible.Count(r => r.Item.Enabled)} ligados · " +
                  $"{_all.Count(r => r.Item.IsOrphan)} órfãos · {_all.Count(r => r.Item.IsMicrosoft)} do Windows";
        ApplyFilter();
    }

    private void ShowBoot(BootReport report)
    {
        BootBars.Clear();
        if (report.Last is not { } last)
        {
            HasBootData = false;
            HeroValue = "—";
            HeroDetail = "Tempo de boot indisponível: " + (report.Unavailable ?? "sem registros.");
            return;
        }
        HasBootData = true;
        HeroValue = BootReport.Seconds(last.TotalMs);
        var parts = new List<string>();
        if (last.MainPathMs is { } main) parts.Add($"área de trabalho em {BootReport.Seconds(main)}");
        if (last.PostBootMs is { } post) parts.Add($"mais {BootReport.Seconds(post)} até o sistema assentar");
        var delay = report.MainDelayOfLastBoot;
        parts.Add(delay is null
            ? "o Windows não apontou nenhum atraso específico nesse boot"
            : $"principal atraso: {delay.Label}, {BootEvents.KindLabel(delay.Kind)} ({BootReport.Seconds(delay.TotalMs)})");
        HeroDetail = $"Último boot ({last.TimeUtc.ToLocalTime():dd/MM HH:mm}): " + string.Join("; ", parts) + ".";

        var recent = report.Boots.TakeLast(12).ToList();
        var max = Math.Max(1, recent.Max(b => b.TotalMs));
        foreach (var b in recent)
            BootBars.Add(new BootBar(Math.Max(4, 90.0 * b.TotalMs / max), b.TimeUtc.ToLocalTime().ToString("dd/MM", System.Globalization.CultureInfo.CurrentCulture),
                BootReport.Seconds(b.TotalMs), ReferenceEquals(b, last)));
    }

    private void ApplyFilter()
    {
        IEnumerable<StartupRow> q = _all;
        if (!_showWindows) q = q.Where(r => !r.Item.IsMicrosoft);
        if (_search.Length > 0)
            q = q.Where(r => r.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
                             r.Publisher.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
                             r.Path.Contains(_search, StringComparison.CurrentCultureIgnoreCase));
        Visible = q.OrderByDescending(r => r.Item.Impact.Level).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ ações (sempre pelo aplicador)

    /// <summary>Ação do catálogo para ligar/desligar o item (null se a origem não permite).</summary>
    internal static (string Key, BrokerLevel Level, Dictionary<string, string> Args)? ToggleRequest(StartupItem i) => i.Source switch
    {
        StartupSource.RunUser => ("boot.startup.user.run@1", BrokerLevel.User, new() { ["entryName"] = i.EntryName }),
        StartupSource.RunMachine => ("boot.startup.machine.run@1", BrokerLevel.Admin, new() { ["entryName"] = i.EntryName }),
        StartupSource.RunMachine32 => ("boot.startup.machine.run32@1", BrokerLevel.Admin, new() { ["entryName"] = i.EntryName }),
        StartupSource.StartupFolderUser => ("boot.startupfolder.user@1", BrokerLevel.User, new() { ["fileName"] = i.EntryName }),
        StartupSource.StartupFolderCommon => ("boot.startupfolder.common@1", BrokerLevel.Admin, new() { ["fileName"] = i.EntryName }),
        StartupSource.ScheduledTask when !i.IsMicrosoft => i.TaskNeedsAdmin
            ? ("boot.task.admin@1", BrokerLevel.Admin, new() { ["taskPath"] = i.EntryName })
            : ("boot.task.user@1", BrokerLevel.User, new() { ["taskPath"] = i.EntryName }),
        _ => null,
    };

    internal static (string Key, BrokerLevel Level)? OrphanRequest(StartupItem i) => i.Source switch
    {
        StartupSource.RunUser => ("boot.orphan.user.run@1", BrokerLevel.User),
        StartupSource.RunMachine => ("boot.orphan.machine.run@1", BrokerLevel.Admin),
        StartupSource.RunMachine32 => ("boot.orphan.machine.run32@1", BrokerLevel.Admin),
        _ => null,
    };

    internal Task ToggleAsync(StartupRow row) => RunAsync(async () =>
    {
        if (Host is null || ToggleRequest(row.Item) is not { } req) return;
        var enable = !row.Item.Enabled;
        var verb = enable ? "Ligar" : "Desligar";
        var text = enable
            ? $"Voltar a abrir \"{row.Name}\" junto com o Windows?"
            : $"Deixar de abrir \"{row.Name}\" junto com o Windows?\n\nO programa continua instalado e pode ser aberto normalmente. " +
              "É o mesmo método do Gerenciador de Tarefas e pode ser desfeito aqui ou na página Aplicar." +
              (req.Level == BrokerLevel.Admin ? "\n\nEste item é do computador inteiro: o Windows vai pedir permissão de administrador." : "");
        await ApplyAsync($"{verb} na inicialização", text, new ActionRequest(req.Key, enable ? "enabled" : "disabled", req.Args), req.Level,
            $"{verb}: {row.Name}");
    });

    internal Task RemoveOrphanAsync(StartupRow row) => RunAsync(async () =>
    {
        if (Host is null || !row.Item.CanRemoveOrphan || OrphanRequest(row.Item) is not { } req) return;
        var text = $"Remover a entrada \"{row.Name}\"?\n\nEla aponta para um programa que não existe mais:\n{row.Path}\n\n" +
                   "Isso não acelera nada; só limpa a lista. O valor completo fica guardado no diário e Desfazer o recria.";
        await ApplyAsync("Remover entrada órfã", text, new ActionRequest(req.Key, null, new Dictionary<string, string> { ["entryName"] = row.Item.EntryName }),
            req.Level, $"Removida: {row.Name}");
    });

    internal Task ServiceManualAsync(StartupRow row) => RunAsync(async () =>
    {
        if (Host is null || row.Item.Source != StartupSource.Service) return;
        var text = $"Pôr o serviço \"{row.Name}\" em Manual?\n\nEle deixa de iniciar sozinho no boot, mas ainda abre quando um programa pedir. " +
                   "Se o programa dele depender do serviço rodando (ex.: sincronização de celular), pode parar de funcionar até desfazer.\n\n" +
                   "Exige administrador. Serviços essenciais do Windows são recusados pelo NEXUS.";
        await ApplyAsync("Serviço em Manual", text, new ActionRequest(Nexus.Boot.ServicePresets.IndividualActionKey, "Manual",
            new Dictionary<string, string> { ["service"] = row.Item.EntryName }), BrokerLevel.Admin, $"Manual: {row.Name}");
    });

    private async Task ApplyAsync(string title, string text, ActionRequest request, BrokerLevel level, string change)
    {
        var r = await Host!.ApplyAsync(title, text, [request], level);
        if (r is null) return; // cancelado
        if (r.Response is { Ok: true, RunId: { } run })
        {
            _lastRun = run;
            _lastLevel = level;
            LastChange = change + " — o efeito aparece no próximo logon/boot.";
        }
        Message = Describe(r);
        await LoadAsync();
    }

    private async Task UndoAsync()
    {
        if (Host is null || _lastRun is not { } run) return;
        var r = await Host.RevertAsync(run, _lastLevel);
        if (r is null) return;
        if (r.Response?.FullyRestored == true) { _lastRun = null; LastChange = ""; }
        Message = r.Message;
        await LoadAsync();
    }

    internal static string Describe(BrokerCallResult r)
    {
        var detail = r.Response?.Results is { Count: > 0 } results ? results[0] : null;
        return detail is null ? r.Message : $"{r.Message} ({detail.Outcome}{(string.IsNullOrEmpty(detail.Detail) ? "" : ": " + detail.Detail)})";
    }
}

/// <summary>Barra do gráfico de boots (altura já normalizada para 0..90 px).</summary>
public sealed record BootBar(double Height, string Label, string Value, bool IsLast);

/// <summary>Linha da lista de inicialização.</summary>
public sealed class StartupRow : ObservableObject
{
    public StartupRow(StartupItem item, StartupPage page)
    {
        Item = item;
        ToggleCommand = new RelayCommand(() => _ = page.ToggleAsync(this), () => CanToggle && !page.Busy);
        RemoveCommand = new RelayCommand(() => _ = page.RemoveOrphanAsync(this), () => CanRemove && !page.Busy);
        ManualCommand = new RelayCommand(() => _ = page.ServiceManualAsync(this), () => IsService && !page.Busy);
    }

    public StartupItem Item { get; }
    public string Name => Item.Name;
    public string Publisher => Item.Publisher.Describe();
    public string Path => Item.ExecutablePath ?? Item.Command ?? "(sem caminho)";
    public string Origin => Item.SourceLabel + (Item.NeedsAdmin ? " · exige administrador" : "");
    public bool IsOn => Item.Enabled;
    public string StateText => Item.Enabled ? "Ligado" : "Desligado";
    public bool CanToggle => Item.CanToggle && StartupPage.ToggleRequest(Item) is not null;
    public bool CanRemove => Item.CanRemoveOrphan;
    public bool IsService => Item.Source == StartupSource.Service;
    public bool IsOrphan => Item.IsOrphan;
    public bool ReadOnly => !CanToggle && !IsService;
    public string ReadOnlyReason => Item.Source switch
    {
        StartupSource.RunOnceUser or StartupSource.RunOnceMachine or StartupSource.RunOnceMachine32 => "Roda uma vez só e some sozinho.",
        StartupSource.ScheduledTask when Item.IsMicrosoft => "Tarefa do Windows: só leitura.",
        _ => "",
    };
    public string FileText => Item.FileExists switch
    {
        true => "",
        false => "Programa não encontrado: entrada órfã",
        null => "Não foi possível conferir o arquivo",
    };
    public string ImpactLabel => Item.Impact.Label;
    public string ImpactReason => Item.Impact.Reason;
    /// <summary>Tom do selo: Bad (alto), Warn (médio), Good (baixo), Muted (desconhecido).</summary>
    public string ImpactTone => Item.Impact.Level switch
    {
        ImpactLevel.High => "Bad",
        ImpactLevel.Medium => "Warn",
        ImpactLevel.Low => "Good",
        _ => "Muted",
    };

    public RelayCommand ToggleCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ManualCommand { get; }
}
