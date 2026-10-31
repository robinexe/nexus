using System.Collections.ObjectModel;
using System.IO;
using Nexus.Boot;
using Nexus.Boot.Inventory;
using Nexus.Boot.Windows;
using Nexus.Broker;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Serviços: lista completa (leitura direta), busca e filtros, presets explicados com antes/depois e Desfazer, e mudança
/// individual do tipo de início. Toda escrita vai pelo aplicador; a lista proibida é aplicada no handler (aqui só avisamos antes).
/// </summary>
public sealed class ServicesPage : ModulePage
{
    private bool _loaded;
    private string _search = "";
    private int _filter; // 0 todos, 1 terceiros, 2 Microsoft, 3 em execução
    private List<ServiceRow> _all = [];
    private IReadOnlyList<ServiceRow> _visible = [];
    private ServiceRow? _selected;
    private StartTypeOption? _chosen;
    private string _summary = "";
    private Guid? _lastRun;
    private string _lastChange = "";

    public ServicesPage()
    {
        RefreshCommand = new RelayCommand(() => _ = RunAsync(LoadAsync), () => !Busy);
        ApplySelectedCommand = new RelayCommand(() => _ = RunAsync(ApplySelectedAsync),
            () => !Busy && _selected is { CanChange: true } s && _chosen is { } c && c.Value != s.Info.StartType);
        UndoCommand = new RelayCommand(() => _ = RunAsync(() => UndoAsync(_lastRun, () => { _lastRun = null; LastChange = ""; })), () => !Busy && _lastRun is not null);
    }

    public override string Title => "Serviços";
    public override string Group => "Sistema";
    public override string Subtitle => "Serviços do Windows explicados, com ajustes seguros e reversíveis.";

    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public IReadOnlyList<ServiceRow> Visible { get => _visible; private set => Set(ref _visible, value); }
    public ObservableCollection<PresetRow> Presets { get; } = [];
    public IReadOnlyList<StartTypeOption> StartTypes { get; } =
    [
        new("Automatic", "Automático"), new("AutomaticDelayed", "Automático (atrasado)"), new("Manual", "Manual"), new("Disabled", "Desativado"),
    ];

    public string SearchText { get => _search; set { if (Set(ref _search, value ?? "")) ApplyFilter(); } }
    public bool FilterAll { get => _filter == 0; set { if (value) SetFilter(0); } }
    public bool FilterThirdParty { get => _filter == 1; set { if (value) SetFilter(1); } }
    public bool FilterMicrosoft { get => _filter == 2; set { if (value) SetFilter(2); } }
    public bool FilterRunning { get => _filter == 3; set { if (value) SetFilter(3); } }

    public ServiceRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Chosen = value is null ? null : StartTypes.FirstOrDefault(o => o.Value == value.Info.StartType);
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selected is not null;
    public StartTypeOption? Chosen { get => _chosen; set => Set(ref _chosen, value); }
    public string LastChange { get => _lastChange; private set { if (Set(ref _lastChange, value)) OnPropertyChanged(nameof(CanUndo)); } }
    public bool CanUndo => _lastRun is not null;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplySelectedCommand { get; }
    public RelayCommand UndoCommand { get; }

    public override Task OnShownAsync() => _loaded ? Task.CompletedTask : RunAsync(LoadAsync);

    private void SetFilter(int f)
    {
        _filter = f;
        OnPropertyChanged(nameof(FilterAll));
        OnPropertyChanged(nameof(FilterThirdParty));
        OnPropertyChanged(nameof(FilterMicrosoft));
        OnPropertyChanged(nameof(FilterRunning));
        ApplyFilter();
    }

    private async Task LoadAsync()
    {
        var selectedName = _selected?.Info.Name;
        var (rows, presets) = await Task.Run(() =>
        {
            var services = new WindowsServiceReader().All();
            var files = new WindowsFileInspector();
            var inventory = new StartupInventory(new WindowsBootRegistryReader(), files, Environment.GetEnvironmentVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var list = services.Select(s =>
            {
                var binary = inventory.ServiceBinary(s);
                var publisher = binary is not null && files.Exists(binary) ? files.Publisher(binary) : FilePublisher.Unknown;
                return new ServiceRow(s, publisher, StartupInventory.IsMicrosoftPublisher(publisher));
            }).OrderBy(r => r.Info.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            IReadOnlyList<ServicePreset> p;
            try { p = ServicePresets.Load(Path.Combine(AppContext.BaseDirectory, "catalog")); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { p = []; }
            return (list, p);
        });
        _loaded = true;
        _all = rows;
        var byName = rows.ToDictionary(r => r.Info.Name, StringComparer.OrdinalIgnoreCase);
        var previous = Presets.ToDictionary(p => p.Preset.Id, StringComparer.Ordinal);
        Presets.Clear();
        foreach (var p in presets)
            Presets.Add(new PresetRow(p, byName, this, previous.TryGetValue(p.Id, out var old) ? old : null));
        Summary = $"{rows.Count} serviços · {rows.Count(r => r.Info.IsRunning)} em execução · {rows.Count(r => r.Info.IsAutomatic)} automáticos · " +
                  $"{rows.Count(r => !r.IsMicrosoft)} de terceiros";
        ApplyFilter();
        if (selectedName is not null && byName.TryGetValue(selectedName, out var again)) Selected = again;
    }

    private void ApplyFilter()
    {
        IEnumerable<ServiceRow> q = _all;
        q = _filter switch
        {
            1 => q.Where(r => !r.IsMicrosoft),
            2 => q.Where(r => r.IsMicrosoft),
            3 => q.Where(r => r.Info.IsRunning),
            _ => q,
        };
        if (_search.Length > 0)
            q = q.Where(r => r.Info.DisplayName.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
                             r.Info.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
                             (r.Info.Description?.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        Visible = q.ToList();
    }

    private async Task ApplySelectedAsync()
    {
        if (Host is null || _selected is not { CanChange: true } s || _chosen is not { } c) return;
        var warning = c.Value == "Disabled"
            ? "\n\nDesativado: o serviço não abre nem quando um programa pedir. Tudo o que depende dele para de funcionar até desfazer."
            : c.Value == "Manual" ? "\n\nManual: o serviço não abre no boot, mas abre quando algum programa pedir." : "";
        var deps = s.Info.Dependencies.Count > 0 ? $"\n\nEle depende de: {string.Join(", ", s.Info.Dependencies)}." : "";
        var text = $"Mudar \"{s.Info.DisplayName}\" ({s.Info.Name}) de {ServicePresets.StartTypeLabel(s.Info.StartType)} para {c.Label}?" +
                   warning + deps + "\n\nNão para nem inicia o serviço agora: vale a partir do próximo boot. Exige administrador; " +
                   "o NEXUS guarda o tipo anterior e Desfazer volta exatamente a ele.";
        var r = await Host.ApplyAsync("Tipo de início do serviço", text,
            [new ActionRequest(ServicePresets.IndividualActionKey, c.Value, new Dictionary<string, string> { ["service"] = s.Info.Name })], BrokerLevel.Admin);
        if (r is null) return;
        if (r.Response is { Ok: true, RunId: { } run })
        {
            _lastRun = run;
            LastChange = $"{s.Info.DisplayName}: {ServicePresets.StartTypeLabel(s.Info.StartType)} → {c.Label}";
        }
        Message = StartupPage.Describe(r);
        await LoadAsync();
    }

    internal Task ApplyPresetAsync(PresetRow preset) => RunAsync(async () =>
    {
        if (Host is null) return;
        var pending = preset.Items.Where(i => i.Pending).ToList();
        if (pending.Count == 0) { Message = "Nada a mudar: este preset já está aplicado (ou os serviços não existem neste PC)."; return; }
        var lines = string.Join("\n", pending.Select(i => $"• {i.DisplayName}: {i.Before} → {i.After}"));
        var text = $"{preset.Title}\n\n{lines}\n\n{preset.Honesty}\n\nExige administrador (o Windows cria um ponto de restauração). " +
                   "Desfazer volta cada serviço ao tipo anterior.";
        var r = await Host.ApplyAsync("Aplicar preset de serviços", text,
            pending.Select(i => new ActionRequest(i.Item.ActionKey)).ToList(), BrokerLevel.Admin);
        if (r is null) return;
        if (r.Response is { Ok: true, RunId: { } run }) preset.Applied(run);
        Message = r.Message;
        await LoadAsync();
    });

    internal Task UndoPresetAsync(PresetRow preset) => RunAsync(() => UndoAsync(preset.RunId, preset.Undone));

    private async Task UndoAsync(Guid? runId, Action onDone)
    {
        if (Host is null || runId is not { } run) return;
        var r = await Host.RevertAsync(run, BrokerLevel.Admin);
        if (r is null) return;
        if (r.Response?.FullyRestored == true) onDone();
        Message = r.Message;
        await LoadAsync();
    }
}

public sealed record StartTypeOption(string Value, string Label);

/// <summary>Linha da lista de serviços.</summary>
public sealed class ServiceRow
{
    public ServiceRow(ServiceInfo info, FilePublisher publisher, bool isMicrosoft)
    {
        Info = info;
        PublisherText = publisher.Describe();
        IsMicrosoft = isMicrosoft;
        Protection = ServicePolicy.Check(info.Name, null)
                     ?? (info.IsPerUser ? "Serviço por usuário: o Windows recria a cada logon; não é alterado." : null);
    }

    public ServiceInfo Info { get; }
    public string PublisherText { get; }
    public bool IsMicrosoft { get; }
    public string Origin => IsMicrosoft ? "Microsoft" : "Terceiro";
    public string? Protection { get; }
    public bool CanChange => Protection is null && Nexus.Boot.Handlers.BootServiceStartTypeHandler.IsValidName(Info.Name);
    public string StartTypeText => ServicePresets.StartTypeLabel(Info.StartType);
    public string StateText => ServicePresets.StateLabel(Info.State);
    public bool IsRunning => Info.IsRunning;
    public string DescriptionText => string.IsNullOrWhiteSpace(Info.Description) ? "Sem descrição." : Info.Description!;
    public string AccountText => string.IsNullOrWhiteSpace(Info.Account) ? "—" : Info.Account!;
    public string DependenciesText => Info.Dependencies.Count == 0 ? "Nenhuma" : string.Join(", ", Info.Dependencies);
    public string ProtectionText => Protection ?? (Info.Name.Equals("wuauserv", StringComparison.OrdinalIgnoreCase)
        ? "Windows Update: pode ir para Manual, nunca Desativado."
        : "");
}

/// <summary>Preset na página: cada item com antes (lido agora) e depois (proposto).</summary>
public sealed class PresetRow : ObservableObject
{
    private Guid? _runId;
    private string _status = "";

    public PresetRow(ServicePreset preset, IReadOnlyDictionary<string, ServiceRow> services, ServicesPage page, PresetRow? previous)
    {
        Preset = preset;
        Items = preset.Items.Select(i => new PresetItemRow(i, services.TryGetValue(i.Service, out var s) ? s : null)).ToList();
        _runId = previous?._runId;
        ApplyCommand = new RelayCommand(() => _ = page.ApplyPresetAsync(this), () => !page.Busy && Items.Any(i => i.Pending));
        UndoCommand = new RelayCommand(() => _ = page.UndoPresetAsync(this), () => !page.Busy && _runId is not null);
        var pending = Items.Count(i => i.Pending);
        _status = _runId is not null ? "Aplicado pelo NEXUS (pode desfazer)." :
            pending == 0 ? "Já está assim neste PC." : $"{pending} de {Items.Count} serviços mudariam.";
    }

    public ServicePreset Preset { get; }
    public string Title => Preset.Title;
    public string Summary => Preset.Summary;
    public string ForWho => "Para quem: " + Preset.ForWho;
    public string Honesty => Preset.Honesty;
    public IReadOnlyList<PresetItemRow> Items { get; }
    public Guid? RunId => _runId;
    public bool CanUndo => _runId is not null;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand UndoCommand { get; }

    internal void Applied(Guid run)
    {
        _runId = run;
        OnPropertyChanged(nameof(CanUndo));
        Status = "Aplicado pelo NEXUS (pode desfazer).";
    }

    internal void Undone()
    {
        _runId = null;
        OnPropertyChanged(nameof(CanUndo));
        Status = "Desfeito.";
    }
}

public sealed class PresetItemRow(ServicePresetItem item, ServiceRow? service)
{
    public ServicePresetItem Item { get; } = item;
    public string DisplayName => service?.Info.DisplayName ?? Item.Service;
    public bool Exists => service is not null;
    public string Before => service is null ? "não existe neste PC" : ServicePresets.StartTypeLabel(service.Info.StartType);
    public string After => ServicePresets.StartTypeLabel(Item.StartType);
    public bool Pending => service is not null && service.Info.StartType != Item.StartType;
    public string Why => Item.Why;
    public string Line => $"{DisplayName}: {Before} → {After}" + (Pending ? "" : " (sem mudança)");
}
