using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Nexus.Broker;
using Nexus.Programs;
using Nexus.Programs.Inventory;
using Nexus.Programs.Leftovers;
using Nexus.Programs.Uninstall;
using Nexus.Programs.Updates;

namespace Nexus.Desktop.ViewModels;

/// <summary>Linha da lista de programas.</summary>
public sealed class ProgramRow(InstalledProgram program)
{
    public InstalledProgram Program { get; } = program;
    public string Name => Program.Name;
    public string Publisher => Program.Publisher ?? "—";
    public string Version => Program.Version ?? "—";
    public string Size => ProgramInventory.FormatSize(Program.SizeBytes);
    public string Installed => Program.InstallDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—";
    public string Scope => Program.ScopeText;
    public string Location => Program.InstallLocation ?? "não informado pelo instalador";
    public string Kind => Program.IsMsi ? "Windows Installer (MSI)" : "Instalador próprio do fornecedor";
    public string Uninstaller => Program.UninstallString ?? "—";
    public string Quiet => Program.QuietUninstallString is null ? "não" : "sim (o NEXUS não usa: abre sempre o desinstalador visível)";
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
}

public sealed class StoreAppRow(StoreApp app)
{
    public string Name => app.DisplayName;
    public string Publisher => app.Publisher is { } p && p.StartsWith("CN=", StringComparison.Ordinal) ? p[3..].Split(',')[0] : app.Publisher ?? "—";
    public string Version => app.Version ?? "—";
    public string Package => app.PackageFullName;
}

/// <summary>Linha de sobra (marcável).</summary>
public sealed class LeftoverRow(LeftoverCandidate candidate) : ObservableObject
{
    private bool _isChecked = candidate.Recommended;
    public LeftoverCandidate Candidate { get; } = candidate;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public string Confidence => $"{Candidate.Confidence} · {Candidate.Score}";
    public string Kind => Candidate.KindText;
    public string Path => Candidate.DisplayPath;
    public string Size => Candidate.Kind == LeftoverKind.Folder ? $"{ProgramInventory.FormatSize(Candidate.SizeBytes)} · {Candidate.FileCount ?? 0} arq." : "—";
    public string Reason => Candidate.Reason;
    public bool High => Candidate.Score >= 75;
    public bool Low => Candidate.Score < 50;
}

public sealed class ManagerRow(PackageManagerStatus status) : ObservableObject
{
    private string _detail = "";
    public PackageManagerStatus Status { get; } = status;
    public string Name => Status.DisplayName;
    public string State => Status.StatusText;
    public bool Installed => Status.Installed;
    public string Explanation => Status.Explanation;
    public string Detail { get => _detail; set => Set(ref _detail, value); }
}

public sealed class UpdateRow(PackageUpdate update) : ObservableObject
{
    private bool _isChecked = update.CanUpgrade;
    public PackageUpdate Update { get; } = update;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value && Update.CanUpgrade); }
    public bool CanUpgrade => Update.CanUpgrade;
    public string Name => Update.Name;
    public string Id => Update.Id;
    public string Current => Update.Current;
    public string Available => Update.Available;
    public string Manager => PackageManagerInfo.DisplayName(Update.Manager) + (Update.Source is { } s && Update.Manager == PackageManagerKind.Winget ? $" · {s}" : "");
    public string Note => Update.IdTruncated ? "ID cortado pelo winget: atualize pelo próprio winget"
        : Update.Pinned ? "Fixado (pinned): o gerenciador não atualiza sem você liberar"
        : Update.Manager == PackageManagerKind.Winget && string.IsNullOrWhiteSpace(Update.Source) ? "Sem origem conhecida"
        : Update.RequiresExplicit ? "Exige pedido explícito (o NEXUS pede por ID)"
        : "";
}

/// <summary>
/// Programas e atualizações: inventário somente leitura (chaves Uninstall + Store opcional), desinstalador OFICIAL visível
/// (exceção controlada, depois de confirmar), varredura de sobras e remoção delas pelo aplicador (handler ProgramLeftover,
/// reversível), e atualizações por winget/Chocolatey/Scoop/npm numa janela visível do próprio gerenciador.
/// </summary>
public sealed class ProgramsPage : ModulePage
{
    public const int SlotsPerScope = 16;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly IProcessRunner _runner = new HiddenProcessRunner();
    private List<InstalledProgram> _all = [];
    private int _tab;
    private string _search = "";
    private int _sortIndex;
    private ProgramRow? _selected;
    private bool _loaded;
    private bool _includeStore;
    private string _storeHeadline = "";
    private InstalledProgram? _leftoverTarget;
    private string _leftoversTitle = "Nenhum programa escolhido ainda";
    private string _leftoversHeadline = "—";
    private string _updatesHeadline = "Ainda não verificado";
    private string _updatesDetail = "Clique em \"Procurar atualizações\". O NEXUS só lê: pergunta a cada gerenciador o que tem versão nova.";
    private string _programsHeadline = "—";
    private string _programsDetail = "";
    private readonly List<(Guid Run, BrokerLevel Level)> _lastRuns = [];

    public ProgramsPage()
    {
        RefreshCommand = new RelayCommand(() => _ = RunAsync(LoadProgramsAsync), () => !Busy);
        UninstallCommand = new RelayCommand(() => _ = RunAsync(UninstallAsync), () => !Busy && Selected is not null);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        LeftoversForSelectedCommand = new RelayCommand(() => { if (Selected is { } s) { _leftoverTarget = s.Program; IsLeftoversTab = true; _ = RunAsync(ScanLeftoversAsync); } },
            () => !Busy && Selected is not null);
        ScanLeftoversCommand = new RelayCommand(() => _ = RunAsync(ScanLeftoversAsync), () => !Busy && _leftoverTarget is not null);
        RemoveLeftoversCommand = new RelayCommand(() => _ = RunAsync(RemoveLeftoversAsync), () => !Busy && Leftovers.Any(l => l.IsChecked));
        UndoLeftoversCommand = new RelayCommand(() => _ = RunAsync(UndoLeftoversAsync), () => !Busy && _lastRuns.Count > 0);
        CheckUpdatesCommand = new RelayCommand(() => _ = RunAsync(CheckUpdatesAsync), () => !Busy);
        UpdateSelectedCommand = new RelayCommand(() => StartUpdates(Updates.Where(u => u.IsChecked && u.CanUpgrade).ToList(), "Atualizar selecionados"),
            () => !Busy && Updates.Any(u => u.IsChecked && u.CanUpgrade));
        UpdateAllCommand = new RelayCommand(() => StartUpdates(Updates.Where(u => u.CanUpgrade).ToList(), "Atualizar tudo"), () => !Busy && Updates.Any(u => u.CanUpgrade));
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Message)) OnPropertyChanged(nameof(HasMessage)); };
    }

    public override string Title => "Programas e atualizações";
    public override string Group => "Limpar";
    public override string Subtitle => "Desinstale o que não usa, limpe o que sobrou e mantenha o resto atualizado.";

    // ------------------------------------------------------------------ abas

    public bool IsProgramsTab { get => _tab == 0; set { if (value) SetTab(0); } }
    public bool IsUpdatesTab { get => _tab == 1; set { if (value) SetTab(1); } }
    public bool IsLeftoversTab { get => _tab == 2; set { if (value) SetTab(2); } }

    private void SetTab(int tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        OnPropertyChanged(nameof(IsProgramsTab));
        OnPropertyChanged(nameof(IsUpdatesTab));
        OnPropertyChanged(nameof(IsLeftoversTab));
    }

    public string ProgramsTabLabel => _loaded ? $"Programas · {_all.Count}" : "Programas";
    public string UpdatesTabLabel => Updates.Count > 0 ? $"Atualizações · {Updates.Count}" : "Atualizações";
    public string LeftoversTabLabel => Leftovers.Count > 0 ? $"Sobras · {Leftovers.Count}" : "Sobras";

    public bool HasMessage => Message.Length > 0;

    private void Say(string message) => Message = message;

    // ------------------------------------------------------------------ programas

    public ObservableCollection<ProgramRow> Programs { get; } = [];
    public ObservableCollection<StoreAppRow> StoreApps { get; } = [];
    public IReadOnlyList<string> SortOptions { get; } = ["Nome (A–Z)", "Maior primeiro", "Instalados por último", "Editor"];

    public string Search { get => _search; set { if (Set(ref _search, value ?? "")) ApplyFilter(); } }
    public int SortIndex { get => _sortIndex; set { if (Set(ref _sortIndex, value)) ApplyFilter(); } }
    public ProgramRow? Selected { get => _selected; set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); } }
    public bool HasSelection => _selected is not null;
    public string ProgramsHeadline { get => _programsHeadline; private set => Set(ref _programsHeadline, value); }
    public string ProgramsDetail { get => _programsDetail; private set => Set(ref _programsDetail, value); }
    public string StoreHeadline { get => _storeHeadline; private set => Set(ref _storeHeadline, value); }

    public bool IncludeStore
    {
        get => _includeStore;
        set
        {
            if (!Set(ref _includeStore, value)) return;
            if (value) _ = RunAsync(LoadStoreAsync);
            else { StoreApps.Clear(); StoreHeadline = ""; }
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand LeftoversForSelectedCommand { get; }

    public override Task OnShownAsync() => _loaded ? Task.CompletedTask : RunAsync(LoadProgramsAsync);

    private static Task<IReadOnlyList<InstalledProgram>> ReadInventoryAsync() =>
        Task.Run(() => ProgramInventory.Build(new WindowsUninstallRegistrySource().ReadAll()));

    private async Task LoadProgramsAsync()
    {
        var list = await ReadInventoryAsync();
        _all = [.. list];
        _loaded = true;
        var total = _all.Sum(p => p.SizeBytes ?? 0);
        ProgramsHeadline = string.Create(PtBr, $"{_all.Count} programas instalados");
        ProgramsDetail = string.Format(PtBr,
            "{0} declarados pelos instaladores · {1} só deste usuário · {2} sem tamanho informado. Fonte: chaves Uninstall do registro " +
            "(as mesmas de \"Aplicativos instalados\"), sem componentes do sistema e sem atualizações.",
            ProgramInventory.FormatSize(total), _all.Count(p => p.Scope == InstallScope.User), _all.Count(p => p.SizeBytes is null));
        ApplyFilter();
        OnPropertyChanged(nameof(ProgramsTabLabel));
    }

    private void ApplyFilter()
    {
        var keep = Selected?.Program.Id;
        var sort = (ProgramSort)Math.Clamp(SortIndex, 0, 3);
        var rows = ProgramInventory.Sort(ProgramInventory.Filter(_all, Search), sort).Select(p => new ProgramRow(p)).ToList();
        Programs.Clear();
        foreach (var r in rows) Programs.Add(r);
        Selected = Programs.FirstOrDefault(r => r.Program.Id == keep);
    }

    private async Task LoadStoreAsync()
    {
        StoreHeadline = "Lendo os apps da Store…";
        try
        {
            var apps = await Task.Run(() => Nexus.Programs.Inventory.StoreApps.ReadAsync(_runner, PackageManagers.PowerShellPath));
            StoreApps.Clear();
            foreach (var a in apps) StoreApps.Add(new StoreAppRow(a));
            StoreHeadline = string.Create(PtBr, $"{apps.Count} apps da Microsoft Store deste usuário (lidos com Get-AppxPackage, sem frameworks nem apps do sistema). Para remover, use Configurações > Aplicativos.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StoreHeadline = $"Não foi possível ler os apps da Store: {ex.Message}";
        }
    }

    private void OpenSettings()
    {
        try { UninstallLauncher.OpenWindowsAppsSettings(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say($"Não foi possível abrir Configurações: {ex.Message}"); }
    }

    private async Task UninstallAsync()
    {
        if (Host is null || Selected is not { } row) return;
        var p = row.Program;
        if (p.NoRemove) { Say($"{p.Name} se registrou como \"não removível\" pelo próprio instalador. Use Configurações > Aplicativos."); return; }
        var cmd = UninstallLauncher.Prepare(p.UninstallString, out var error);
        if (cmd is null) { Say(error ?? "Desinstalador inválido."); return; }

        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Abrir o desinstalador oficial de {p.Name}?\n\n")
            .Append("Quem remove o programa é o desinstalador do próprio fornecedor, numa janela que você vê e controla:\n")
            .Append(CultureInfo.InvariantCulture, $"{cmd.Display}\n\n")
            .Append("O NEXUS não consegue desfazer uma desinstalação: para voltar, será preciso instalar o programa de novo.\n\n")
            .Append("As mudanças de sistema feitas pelo próprio NEXUS criam um ponto de restauração automático. Aqui quem age é o fornecedor; ")
            .Append("se quiser uma garantia extra, crie um ponto antes (menu Iniciar > \"Criar um ponto de restauração\").\n\n")
            .Append("Quando o desinstalador terminar, o NEXUS procura as sobras (pastas e chaves que ficaram para trás).")
            .ToString();
        if (!Host.Confirm($"Desinstalar {p.Name}", text)) { Say("Cancelado. Nada foi alterado."); return; }

        System.Diagnostics.Process? process;
        try { process = UninstallLauncher.Start(cmd); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.FileNotFoundException or InvalidOperationException)
        {
            Say($"O desinstalador não abriu: {ex.Message}");
            return;
        }
        _leftoverTarget = p;
        LeftoversTitle = $"Sobras de {p.Name}";
        Say($"Desinstalador de {p.Name} aberto. Siga as instruções dele; quando terminar, o NEXUS confere a lista e procura as sobras.");
        if (process is not null) _ = WatchUninstallerAsync(process, p);
        await Task.CompletedTask;
    }

    private async Task WatchUninstallerAsync(System.Diagnostics.Process process, InstalledProgram p)
    {
        try
        {
            using (process) await process.WaitForExitAsync();
            await Task.Delay(1500); // o registro às vezes é atualizado logo depois de a janela fechar
            await RunAsync(async () =>
            {
                await LoadProgramsAsync();
                if (StillInstalled(p))
                {
                    Say($"O desinstalador fechou, mas {p.Name} ainda aparece na lista. Alguns desinstaladores continuam em outra janela: termine por lá e clique em \"Procurar sobras\".");
                    return;
                }
                IsLeftoversTab = true;
                await ScanLeftoversAsync();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private bool StillInstalled(InstalledProgram p) =>
        _all.Any(x => x.Id == p.Id || (string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Version, p.Version, StringComparison.OrdinalIgnoreCase)));

    // ------------------------------------------------------------------ sobras

    public ObservableCollection<LeftoverRow> Leftovers { get; } = [];
    public string LeftoversTitle { get => _leftoversTitle; private set => Set(ref _leftoversTitle, value); }
    public string LeftoversHeadline { get => _leftoversHeadline; private set => Set(ref _leftoversHeadline, value); }
    public bool CanUndo => _lastRuns.Count > 0;
    public RelayCommand ScanLeftoversCommand { get; }
    public RelayCommand RemoveLeftoversCommand { get; }
    public RelayCommand UndoLeftoversCommand { get; }

    private async Task ScanLeftoversAsync()
    {
        if (_leftoverTarget is not { } target) return;
        LeftoversTitle = $"Sobras de {target.Name}";
        await LoadProgramsAsync();
        Leftovers.Clear();
        if (StillInstalled(target))
        {
            LeftoversHeadline = "Ainda instalado";
            Say($"{target.Name} ainda está instalado. Desinstale primeiro: o NEXUS só procura sobras de programas que já saíram da lista.");
            OnPropertyChanged(nameof(LeftoversTabLabel));
            return;
        }
        var installed = _all;
        var found = await Task.Run(() => LeftoverScanner.Scan(target, installed,
            new LeftoverRoots(WindowsLeftoverRoots.MachineRoots(), WindowsLeftoverRoots.CurrentUserRoots()), new WindowsRegistryKeyReader()));
        foreach (var f in found) Leftovers.Add(new LeftoverRow(f));
        LeftoversHeadline = found.Count == 0 ? "Nada sobrou" : LeftoverScanner.Summary(found);
        Say(found.Count == 0
            ? $"Nenhuma sobra de {target.Name} encontrada nas pastas e chaves que o NEXUS confere."
            : "Confira a lista. As de confiança alta já vêm marcadas; as baixas, não. Nada sai do lugar sem você confirmar.");
        OnPropertyChanged(nameof(LeftoversTabLabel));
    }

    private async Task RemoveLeftoversAsync()
    {
        if (Host is null) return;
        var chosen = Leftovers.Where(l => l.IsChecked).Select(l => l.Candidate).ToList();
        if (chosen.Count == 0) return;
        var user = chosen.Where(c => !c.MachineScope).ToList();
        var machine = chosen.Where(c => c.MachineScope).ToList();
        if (user.Count > SlotsPerScope || machine.Count > SlotsPerScope)
        {
            Say($"Escolha no máximo {SlotsPerScope} sobras do perfil e {SlotsPerScope} de todos os usuários por vez.");
            return;
        }

        var moved = 0;
        var failed = new List<string>();
        foreach (var (group, level, slot, label) in new[]
                 {
                     (user, BrokerLevel.User, "user", "do seu perfil"),
                     (machine, BrokerLevel.Admin, "machine", "de todos os usuários (pede administrador e cria um ponto de restauração)"),
                 })
        {
            if (group.Count == 0) continue;
            var requests = group.Select((c, i) => new ActionRequest($"programs.leftover.{slot}.{i + 1:00}@1", null,
                new Dictionary<string, string> { ["kind"] = c.Kind == LeftoverKind.Folder ? "file" : "registry", ["path"] = c.Path })).ToList();
            var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Mover {group.Count} sobra(s) {label} para a quarentena do NEXUS?\n\n");
            foreach (var c in group.Take(12)) text.Append(CultureInfo.InvariantCulture, $"• {c.DisplayPath}\n");
            if (group.Count > 12) text.Append(CultureInfo.InvariantCulture, $"• … e mais {group.Count - 12}\n");
            text.Append("\nNada é apagado: pastas vão para a quarentena com a lista de arquivos conferida, e chaves do registro são guardadas com todos os valores. ")
                .Append("Você pode desfazer aqui (botão Desfazer) ou na página Aplicar. Liberar espaço não deixa o PC mais rápido.");
            var r = await Host.ApplyAsync("Remover sobras", text.ToString(), requests, level);
            if (r is null) continue;
            if (r.Response is { Ok: true, RunId: { } run })
            {
                _lastRuns.Add((run, level));
                var results = r.Response.Results ?? [];
                moved += results.Count(x => x.Outcome == "Verified");
                failed.AddRange(results.Where(x => x.Outcome != "Verified").Select(x => $"{x.Outcome}: {x.Detail}"));
            }
            else failed.Add(r.Message);
        }
        OnPropertyChanged(nameof(CanUndo));
        await ScanLeftoversAsync();
        Say(failed.Count == 0
            ? string.Create(PtBr, $"{moved} sobra(s) na quarentena. Se algo fizer falta, use Desfazer.")
            : string.Create(PtBr, $"{moved} na quarentena; {failed.Count} não: {string.Join(" | ", failed.Take(3))}"));
    }

    private async Task UndoLeftoversAsync()
    {
        if (Host is null || _lastRuns.Count == 0) return;
        foreach (var (run, level) in Enumerable.Reverse(_lastRuns).ToList())
        {
            var r = await Host.RevertAsync(run, level);
            if (r is null) return; // cancelado: mantém para tentar de novo
            if (r.Response?.FullyRestored == true) _lastRuns.Remove((run, level));
            else { Say($"Desfazer não terminou: {r.Message}"); break; }
        }
        OnPropertyChanged(nameof(CanUndo));
        if (_leftoverTarget is not null) await ScanLeftoversAsync();
        if (_lastRuns.Count == 0) Say("Desfeito: as sobras voltaram para onde estavam.");
    }

    // ------------------------------------------------------------------ atualizações

    public ObservableCollection<ManagerRow> Managers { get; } = [];
    public ObservableCollection<UpdateRow> Updates { get; } = [];
    public string UpdatesHeadline { get => _updatesHeadline; private set => Set(ref _updatesHeadline, value); }
    public string UpdatesDetail { get => _updatesDetail; private set => Set(ref _updatesDetail, value); }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand UpdateSelectedCommand { get; }
    public RelayCommand UpdateAllCommand { get; }

    private async Task CheckUpdatesAsync()
    {
        UpdatesHeadline = "Verificando…";
        UpdatesDetail = "Perguntando a cada gerenciador (pode levar até alguns minutos na primeira vez).";
        Managers.Clear();
        Updates.Clear();
        var statuses = await Task.WhenAll(PackageManagers.All.Select(k => Task.Run(() => PackageManagers.DetectAsync(k, _runner))));
        var rows = statuses.Select(s => new ManagerRow(s)).ToList();
        foreach (var r in rows) Managers.Add(r);
        var lists = await Task.WhenAll(statuses.Select(s => Task.Run(() => PackageManagers.ListUpdatesAsync(s, _runner))));
        foreach (var (row, list) in rows.Zip(lists))
        {
            row.Detail = !row.Installed ? "Não instalado neste PC."
                : list.Problem is { } problem ? problem
                : list.Updates.Count == 0 ? "Tudo em dia."
                : string.Create(PtBr, $"{list.Updates.Count} com versão nova.");
            foreach (var u in list.Updates) Updates.Add(new UpdateRow(u));
        }
        var n = Updates.Count;
        UpdatesHeadline = n == 0 ? "Tudo em dia" : string.Create(PtBr, $"{n} {(n == 1 ? "atualização disponível" : "atualizações disponíveis")}");
        UpdatesDetail = n == 0
            ? "Nenhum gerenciador encontrado relatou versão nova."
            : string.Create(PtBr, $"{Updates.Count(u => u.CanUpgrade)} podem ser pedidas daqui. A atualização abre a janela do próprio gerenciador; o NEXUS não consegue desfazê-la.");
        OnPropertyChanged(nameof(UpdatesTabLabel));
    }

    private void StartUpdates(List<UpdateRow> rows, string title)
    {
        if (Host is null || rows.Count == 0) return;
        var groups = rows.GroupBy(r => r.Update.Manager).ToList();
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Atualizar {rows.Count} pacote(s)?\n\n");
        foreach (var g in groups)
        {
            text.Append(CultureInfo.InvariantCulture, $"{PackageManagerInfo.DisplayName(g.Key)}:\n");
            foreach (var r in g.Take(15)) text.Append(CultureInfo.InvariantCulture, $"• {r.Name}  {r.Current} → {r.Available}\n");
            if (g.Count() > 15) text.Append(CultureInfo.InvariantCulture, $"• … e mais {g.Count() - 15}\n");
        }
        text.Append("\nCada gerenciador abre numa janela visível do PowerShell, e é ele quem baixa e instala. ")
            .Append("O NEXUS não consegue desfazer uma atualização.");
        if (groups.Any(g => g.Key == PackageManagerKind.Chocolatey))
            text.Append(" O Chocolatey pede administrador: o Windows vai mostrar o aviso antes de abrir a janela dele.");
        if (!Host.Confirm(title, text.ToString())) { Say("Cancelado. Nada foi alterado."); return; }

        var opened = new List<string>();
        foreach (var g in groups)
        {
            var status = Managers.FirstOrDefault(m => m.Status.Kind == g.Key)?.Status;
            if (status is null) continue;
            try
            {
                PackageManagers.StartUpgradeWindow(status, g.Select(r => r.Update).ToList())?.Dispose();
                opened.Add(status.DisplayName);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                Say($"{status.DisplayName}: a janela não abriu ({ex.Message}).");
            }
        }
        if (opened.Count > 0)
            Say($"Janela aberta: {string.Join(", ", opened)}. Quando terminar, clique em \"Procurar atualizações\" para conferir.");
    }
}
