using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Nexus.Broker;
using Nexus.SystemTools;
using Nexus.SystemTools.Appx;
using Nexus.SystemTools.Drivers;
using Nexus.SystemTools.Restore;
using Nexus.SystemTools.Wifi;

namespace Nexus.Desktop.ViewModels;

/// <summary>Linha com uma ação reversível (remover com backup) e o "Desfazer" da execução que a aplicou.</summary>
public sealed class SystemActionRow : ObservableObject
{
    private string _status = "";
    private string _tone = "muted";
    private Guid? _runId;
    private bool _applied;

    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";
    /// <summary>Motivo para não oferecer a remoção (proteção), ou null.</summary>
    public string? Blocked { get; init; }
    public bool CanApply => Blocked is null && !_applied;
    public string Status { get => _status; set => Set(ref _status, value); }
    /// <summary>good, bad, trace, signal ou muted (cor do selo).</summary>
    public string Tone { get => _tone; set => Set(ref _tone, value); }
    public Guid? RunId { get => _runId; set { if (Set(ref _runId, value)) OnPropertyChanged(nameof(CanRevert)); } }
    public bool Applied { get => _applied; set { if (Set(ref _applied, value)) { OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(CanRevert)); } } }
    public bool CanRevert => _applied && _runId is not null;
    public ICommand? ApplyCommand { get; set; }
    public ICommand? RevertCommand { get; set; }
    public ICommand? ExtraCommand { get; set; }
    public string ExtraLabel { get; set; } = "";
    public object? Tag { get; init; }
}

public sealed record RestorePointRow(string When, string Description, string Kind);

/// <summary>
/// Ferramentas do sistema: rede (DNS/ARP), perfis Wi-Fi, drivers antigos no repositório, apps pré-instalados e pontos de
/// restauração. Leitura direto da biblioteca (somente leitura); toda mudança vai pelo aplicador (Host.ApplyAsync/RevertAsync).
/// </summary>
public sealed class SystemToolsPage : ModulePage
{
    public override string Title => "Ferramentas do sistema";
    public override string Group => "Sistema";
    public override string Subtitle => "Rede, drivers, apps do Windows e pontos de restauração.";

    private const string DnsAction = "system.network.dns-flush@1", ArpAction = "system.network.arp-flush@1";
    private const string WifiAction = "system.wifi.profile-remove@1", DriverAction = "system.driver.package-remove@1";
    private const string DriverBackupAction = "system.driver.backup-all@1", RestoreAction = "system.restore.create-point@1";
    private const string AppxAction = "system.appx.remove-user@1";

    private bool _loaded;
    private string _dns = "—", _arp = "—", _wifiNote = "", _driverNote = "", _appxNote = "", _restoreNote = "", _protection = "", _driverBackup = "";
    private bool _onlyCandidates = true;
    private List<SystemActionRow> _allDrivers = [];
    private int _driverCandidates;

    public SystemToolsPage()
    {
        RefreshCommand = new RelayCommand(() => _ = RunAsync(LoadAsync), () => !Busy);
        FlushDnsCommand = new RelayCommand(() => _ = RunAsync(() => EphemeralAsync("Limpar o cache DNS",
            "Esvaziar o cache DNS do Windows?\n\nO que faz: os nomes de sites voltam a ser consultados no servidor DNS na próxima vez que forem usados. " +
            "Ajuda quando um site mudou de endereço ou não abre depois de trocar de DNS.\nGanho de desempenho: nenhum (o cache existe para evitar consultas).\n" +
            "Risco: nenhum conhecido.\nDesfazer: não há o que desfazer; o cache se refaz sozinho.", DnsAction, BrokerLevel.User)), () => !Busy);
        FlushArpCommand = new RelayCommand(() => _ = RunAsync(() => EphemeralAsync("Limpar o cache ARP",
            "Esvaziar a tabela ARP (endereços físicos da rede local)?\n\nAjuda depois de trocar o roteador ou quando um aparelho da rede não é encontrado.\n" +
            "Ganho de desempenho: nenhum.\nRisco: a conexão pode pausar por uma fração de segundo.\nDesfazer: não há o que desfazer; as entradas voltam em segundos.\n\n" +
            "Exige administrador: o Windows vai pedir confirmação.", ArpAction, BrokerLevel.Admin)), () => !Busy);
        OpenNetworkCommand = new RelayCommand(OpenNetworkSettings);
        BackupDriversCommand = new RelayCommand(() => _ = RunAsync(() => EphemeralAsync("Backup de drivers",
            "Copiar todos os drivers de terceiros instalados (pnputil /export-driver *) para a pasta de dados do NEXUS?\n\n" +
            "Útil antes de formatar ou atualizar drivers. Não altera nada no sistema; ocupa espaço em disco.\n\nExige administrador.", DriverBackupAction, BrokerLevel.Admin)), () => !Busy);
        OpenDriverBackupsCommand = new RelayCommand(OpenDriverBackups);
        CreateRestorePointCommand = new RelayCommand(() => _ = RunAsync(() => EphemeralAsync("Criar ponto de restauração",
            "Pedir ao Windows um ponto de restauração agora?\n\nEle guarda arquivos de sistema, registro e drivers para voltar atrás pela Restauração do Sistema. " +
            "Exige a Proteção do Sistema ligada. O Windows cria no máximo um ponto a cada 24 horas; se já houver um recente, ele vale.\n\nExige administrador.",
            RestoreAction, BrokerLevel.Admin)), () => !Busy);
        OpenRestoreCommand = new RelayCommand(OpenSystemRestore);
        OpenProtectionCommand = new RelayCommand(OpenSystemProtection);
    }

    // ------------------------------------------------------------------ estado lido

    public string DnsEntries { get => _dns; private set => Set(ref _dns, value); }
    public string ArpEntries { get => _arp; private set => Set(ref _arp, value); }
    public ObservableCollection<SystemActionRow> WifiProfiles { get; } = [];
    public string WifiNote { get => _wifiNote; private set => Set(ref _wifiNote, value); }
    public ObservableCollection<SystemActionRow> Drivers { get; } = [];
    public string DriverNote { get => _driverNote; private set => Set(ref _driverNote, value); }
    public string DriverBackupInfo { get => _driverBackup; private set => Set(ref _driverBackup, value); }
    public int DriverCandidates { get => _driverCandidates; private set => Set(ref _driverCandidates, value); }
    public bool OnlyCandidates
    {
        get => _onlyCandidates;
        set { if (Set(ref _onlyCandidates, value)) FillDrivers(); }
    }
    public ObservableCollection<SystemActionRow> Apps { get; } = [];
    public string AppxNote { get => _appxNote; private set => Set(ref _appxNote, value); }
    public ObservableCollection<RestorePointRow> RestorePoints { get; } = [];
    public string RestoreNote { get => _restoreNote; private set => Set(ref _restoreNote, value); }
    public string ProtectionStatus { get => _protection; private set => Set(ref _protection, value); }

    public ICommand RefreshCommand { get; }
    public ICommand FlushDnsCommand { get; }
    public ICommand FlushArpCommand { get; }
    public ICommand OpenNetworkCommand { get; }
    public ICommand BackupDriversCommand { get; }
    public ICommand OpenDriverBackupsCommand { get; }
    public ICommand CreateRestorePointCommand { get; }
    public ICommand OpenRestoreCommand { get; }
    public ICommand OpenProtectionCommand { get; }

    public override async Task OnShownAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RunAsync(LoadAsync);
    }

    private static string AdminDataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.Admin))!;
    private static string N(int? v) => v is null ? "n/d" : v.Value.ToString(CultureInfo.CurrentCulture);

    private async Task LoadAsync()
    {
        Message = "Lendo rede, Wi-Fi, drivers, apps e pontos de restauração…";
        var network = Task.Run(SystemToolsReader.Network);
        var wifi = Task.Run(SystemToolsReader.Wifi);
        var drivers = Task.Run(SystemToolsReader.Drivers);
        var apps = Task.Run(SystemToolsReader.Appx);
        var restore = Task.Run(SystemToolsReader.RestorePoints);
        var backups = Task.Run(() => SystemToolsReader.DriverBackups(AdminDataDirectory));

        var n = await network;
        DnsEntries = N(n.DnsEntries);
        ArpEntries = N(n.ArpEntries);
        FillWifi(await wifi);
        FillDriverReading(await drivers);
        var b = await backups;
        DriverBackupInfo = b.Count == 0 ? "Nenhum backup de drivers feito pelo NEXUS ainda." : $"{b.Count} backup(s); o mais recente em {b[0]}";
        FillApps(await apps);
        FillRestore(await restore);
        Message = $"Lido às {DateTime.Now:HH:mm}.";
    }

    // ------------------------------------------------------------------ Wi-Fi

    private void FillWifi(WifiReading r)
    {
        WifiProfiles.Clear();
        foreach (var g in r.Profiles.GroupBy(p => p.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var p = g.First();
            var last = r.LastConnected.TryGetValue(p.Name, out var at) ? $"última conexão {at:dd/MM/yyyy}" : "última conexão desconhecida";
            var scope = p.Scope switch { WifiProfileScope.GroupPolicy => "empresa", WifiProfileScope.CurrentUser => "só este usuário", _ => "todos os usuários" };
            var old = r.LastConnected.TryGetValue(p.Name, out var when) && when < DateTime.Now.AddDays(-180);
            var row = new SystemActionRow
            {
                Key = p.Name, Title = p.Name, Badge = old ? "sem uso há 6+ meses" : "",
                Detail = $"{scope} · {last} · {string.Join(", ", g.Select(x => x.Interface).Distinct())}",
                Blocked = p.Removable ? null : "Definido pela empresa (política de grupo): o NEXUS não remove.",
            };
            row.ApplyCommand = new RelayCommand(() => _ = RunAsync(() => RemoveWifiAsync(row)), () => !Busy && row.CanApply);
            row.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertRowAsync(row, BrokerLevel.Admin)), () => !Busy && row.CanRevert);
            WifiProfiles.Add(row);
        }
        WifiNote = r.Profiles.Count == 0 ? r.Note ?? "Nenhuma rede Wi-Fi salva." : $"{r.Profiles.Select(p => p.Name).Distinct().Count()} rede(s) salva(s).";
    }

    private Task RemoveWifiAsync(SystemActionRow row) => ApplyRowAsync(row, "Remover rede Wi-Fi salva",
        $"Remover a rede Wi-Fi salva \"{row.Title}\"?\n\nO NEXUS guarda antes uma cópia do perfil completo (com a senha), protegida pela criptografia do Windows, " +
        "na pasta de dados do aplicador. Depois remove o perfil: o Windows deixa de se conectar a essa rede sozinho.\n" +
        "Ganho de desempenho: nenhum.\nRisco: se for a rede que você está usando agora, a conexão cai.\n" +
        "Desfazer: reimporta o perfil guardado (botão Desfazer).\n\nExige administrador.",
        new ActionRequest(WifiAction, Arguments: new Dictionary<string, string> { ["wifiProfile"] = row.Key }), BrokerLevel.Admin);

    // ------------------------------------------------------------------ drivers

    private void FillDriverReading(DriverReading r)
    {
        _allDrivers = [];
        if (r.Error is not null) { DriverNote = "Não foi possível ler os drivers: " + r.Error; DriverCandidates = 0; Drivers.Clear(); return; }
        foreach (var p in r.Packages.OrderBy(p => p.ClassName, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.OriginalName, StringComparer.OrdinalIgnoreCase).ThenByDescending(p => p.Version))
        {
            var blocked = DriverRules.CheckRemoval(p, r.Packages, r.InUse);
            var newer = DriverRules.NewerDuplicate(p, r.Packages);
            var inUse = r.InUse is not null && r.InUse.ContainsKey(p.PublishedName);
            var row = new SystemActionRow
            {
                Key = p.PublishedName,
                Title = $"{p.Provider} · {p.OriginalName}",
                Detail = $"{p.ClassName} · versão {p.VersionText}{(p.Date is { } d ? $" de {d:dd/MM/yyyy}" : "")} · {p.PublishedName}" +
                         (newer is not null ? $" · há versão mais nova ({newer.VersionText})" : ""),
                Badge = inUse ? "em uso" : newer is not null ? "versão antiga" : "não usado",
                Blocked = blocked,
                Tag = newer is not null && blocked is null,
            };
            row.Tone = inUse ? "good" : newer is not null ? "trace" : "muted";
            row.ApplyCommand = new RelayCommand(() => _ = RunAsync(() => RemoveDriverAsync(row)), () => !Busy && row.CanApply);
            row.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertRowAsync(row, BrokerLevel.Admin)), () => !Busy && row.CanRevert);
            _allDrivers.Add(row);
        }
        DriverCandidates = _allDrivers.Count(x => x.Tag is true);
        DriverNote = r.InUse is null
            ? $"{r.Packages.Count} pacotes de terceiros. Não foi possível saber quais estão em uso (requer Windows 10 2004+): nenhuma remoção é oferecida."
            : $"{r.Packages.Count} pacotes de terceiros no repositório; {DriverCandidates} versão(ões) antiga(s) sem uso.";
        FillDrivers();
    }

    private void FillDrivers()
    {
        Drivers.Clear();
        foreach (var row in _allDrivers.Where(x => !OnlyCandidates || x.Tag is true || x.Applied)) Drivers.Add(row);
    }

    private Task RemoveDriverAsync(SystemActionRow row) => ApplyRowAsync(row, "Remover driver antigo",
        $"Remover do repositório de drivers o pacote {row.Key} ({row.Title})?\n\n{row.Detail}\n\n" +
        "Antes, o NEXUS exporta o pacote (pnputil /export-driver) para a pasta de dados do aplicador. A remoção é feita sem /force: " +
        "se algum dispositivo usar o pacote, o próprio Windows recusa.\nGanho: espaço em disco; nenhum ganho de desempenho.\n" +
        "Risco: baixo. Um aparelho desconectado que usava este driver pode pedi-lo de novo.\nDesfazer: reinstala do backup (pnputil /add-driver).\n\n" +
        "Exige administrador.",
        new ActionRequest(DriverAction, Arguments: new Dictionary<string, string> { ["driverInf"] = row.Key }), BrokerLevel.Admin);

    private void OpenDriverBackups()
    {
        var root = DriverPackageHandler.BackupRoot(AdminDataDirectory);
        if (!Directory.Exists(root)) { Message = "Ainda não há backups de drivers."; return; }
        try { VendorTools.OpenFolder(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Message = "Não foi possível abrir a pasta: " + ex.Message; }
    }

    // ------------------------------------------------------------------ apps do Windows

    private void FillApps(AppxReading r)
    {
        Apps.Clear();
        if (r.Error is not null) { AppxNote = "Não foi possível ler os apps: " + r.Error; return; }
        var provisioned = r.Provisioned?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedCount = 0;
        foreach (var p in r.Packages.OrderBy(p => AppxCatalog.Classify(p.Name).Class).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (cls, entry) = AppxCatalog.Classify(p.Name);
            if (cls == AppxClass.Protected) { protectedCount++; continue; }
            if (cls == AppxClass.Unknown || entry is null) continue;
            var blocked = AppxCatalog.CheckRemoval(p);
            var prov = provisioned is null ? "" : provisioned.Contains(p.Name) ? " · também provisionado (fica para contas novas)" : "";
            var row = new SystemActionRow
            {
                Key = p.Name, Title = entry.Title, Detail = $"{entry.Note} · {p.Name} {p.Version}{prov}",
                Badge = cls == AppxClass.Safe ? "seguro remover" : "atenção", Blocked = blocked, Tag = p.PackageFamilyName,
            };
            row.Tone = cls == AppxClass.Safe ? "good" : "trace";
            row.ApplyCommand = new RelayCommand(() => _ = RunAsync(() => RemoveAppAsync(row, entry, cls)), () => !Busy && row.CanApply);
            row.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertRowAsync(row, BrokerLevel.User)), () => !Busy && row.CanRevert);
            row.ExtraLabel = "Abrir na Store";
            row.ExtraCommand = new RelayCommand(() => OpenStore(row));
            Apps.Add(row);
        }
        AppxNote = $"{Apps.Count} app(s) da lista de pré-instalados encontrados para este usuário · {protectedCount} componente(s) protegido(s) nunca são removidos" +
                   (provisioned is null ? " · lista de provisionados exige administrador (não lida)." : $" · {provisioned.Count} pacote(s) provisionado(s) (não são removidos).");
    }

    private Task RemoveAppAsync(SystemActionRow row, AppxCatalogEntry entry, AppxClass cls) => ApplyRowAsync(row, "Remover app do Windows",
        $"Remover \"{entry.Title}\" ({row.Key}) para o seu usuário?\n\n{entry.Note}\n" +
        (cls == AppxClass.Warn ? "\nATENÇÃO: este app é útil para muita gente. Remova só se não usa.\n" : "") +
        "\nGanho: espaço e menos atalhos no menu Iniciar; nenhum ganho em jogos.\n" +
        "Desfazer é LIMITADO: o NEXUS reinstala sozinho só se os arquivos do app ainda estiverem no PC (app provisionado ou de outra conta). " +
        "Se não estiverem, será preciso reinstalar pela Microsoft Store (o botão \"Abrir na Store\" leva direto à página).\n" +
        "O pacote provisionado (para contas novas) não é tocado.",
        new ActionRequest(AppxAction, Arguments: new Dictionary<string, string> { ["appxName"] = row.Key }), BrokerLevel.User);

    private void OpenStore(SystemActionRow row)
    {
        if (row.Tag is not string family || !AppxCatalog.IsValidFamilyName(family)) return;
        if (Host?.Confirm("Abrir a Microsoft Store", $"Abrir a página de \"{row.Title}\" na Microsoft Store? A instalação, se você quiser, é feita pela própria Store.") != true) return;
        try { VendorTools.OpenStorePage(family); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException) { Message = "Não foi possível abrir a Store: " + ex.Message; }
    }

    // ------------------------------------------------------------------ pontos de restauração

    private void FillRestore(RestoreReading r)
    {
        RestorePoints.Clear();
        if (r.Points.Items is { } items)
            foreach (var p in items.Take(30))
                RestorePoints.Add(new RestorePointRow(p.Created is { } c ? c.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) : "?", p.Description, RestoreParsers.TypeName(p.Type)));
        RestoreNote = r.Points.Items is null
            ? (r.Points.Error ?? "Não foi possível listar.") + " Abra a Restauração do Sistema para ver os pontos."
            : r.Points.Items.Count == 0 ? "Nenhum ponto de restauração no PC." : $"{r.Points.Items.Count} ponto(s) de restauração.";
        ProtectionStatus = r.DisabledByPolicy ? "Desligada por política (organização)."
            : r.ProtectedDrives is null ? "Estado da Proteção do Sistema não pôde ser lido."
            : r.ProtectedDrives.Count == 0 ? "Proteção do Sistema desligada em todas as unidades: não há como criar pontos."
            : "Proteção do Sistema ligada em: " + string.Join(", ", r.ProtectedDrives.Select(d => d + ":"));
    }

    private void OpenSystemRestore()
    {
        if (Host?.Confirm("Restauração do Sistema", "Abrir a Restauração do Sistema do Windows? Nela você escolhe o ponto; o próprio Windows mostra o que será afetado e pede confirmação antes de reiniciar.") != true) return;
        try { VendorTools.OpenSystemRestore(); }
        catch (System.ComponentModel.Win32Exception ex) { Message = "Não foi possível abrir: " + ex.Message; }
    }

    private void OpenSystemProtection()
    {
        try { VendorTools.OpenSystemProtection(); }
        catch (System.ComponentModel.Win32Exception ex) { Message = "Não foi possível abrir: " + ex.Message; }
    }

    private void OpenNetworkSettings()
    {
        if (Host?.Confirm("Redefinição de rede", "Abrir as Configurações de rede do Windows? Lá fica a \"Redefinição de rede\" (reinstala adaptadores e volta Winsock/TCP-IP ao padrão). " +
                "O NEXUS não faz isso sozinho: exige reinício, apaga configurações manuais de IP/DNS/VPN e não tem como ser desfeito.") != true) return;
        try { VendorTools.OpenNetworkSettings(); }
        catch (System.ComponentModel.Win32Exception ex) { Message = "Não foi possível abrir: " + ex.Message; }
    }

    // ------------------------------------------------------------------ aplicar / desfazer

    private static (string Outcome, string Detail) First(BrokerCallResult r) =>
        r.Response?.Results is { Count: > 0 } items ? (items[0].Outcome, items[0].Detail ?? "") : ("", r.Message);

    private async Task EphemeralAsync(string title, string confirmation, string actionId, BrokerLevel level)
    {
        if (Host is null) return;
        var r = await Host.ApplyAsync(title, confirmation, [new ActionRequest(actionId)], level);
        if (r is null) return;
        var (outcome, detail) = First(r);
        Message = outcome switch
        {
            "Verified" => $"{title}: feito.",
            "AlreadyConfigured" => $"{title}: feito há poucos segundos; nada repetido.",
            "" => r.Message,
            _ => $"{title}: não concluído ({outcome}). {detail}",
        };
        await LoadAsync();
    }

    private async Task ApplyRowAsync(SystemActionRow row, string title, string confirmation, ActionRequest request, BrokerLevel level)
    {
        if (Host is null) return;
        var r = await Host.ApplyAsync(title, confirmation, [request], level);
        if (r is null) return;
        var (outcome, detail) = First(r);
        if (outcome == "Verified")
        {
            row.RunId = r.Response?.RunId;
            row.Applied = true;
            row.Status = "removido · backup guardado";
            row.Tone = "good";
            Message = $"{row.Title}: removido. Use Desfazer para voltar.";
        }
        else
        {
            row.Status = outcome.Length == 0 ? r.Message : $"{Translate(outcome)}: {detail}";
            row.Tone = "bad";
            Message = row.Status;
        }
    }

    private async Task RevertRowAsync(SystemActionRow row, BrokerLevel level)
    {
        if (Host is null || row.RunId is not { } run) return;
        var r = await Host.RevertAsync(run, level);
        if (r is null) return;
        var (outcome, detail) = First(r);
        if (r.Response?.FullyRestored == true)
        {
            row.Applied = false;
            row.RunId = null;
            row.Status = "restaurado";
            row.Tone = "signal";
            Message = $"{row.Title}: restaurado.";
        }
        else
        {
            row.Status = $"não restaurado: {detail}";
            row.Tone = "bad";
            Message = row.Status;
        }
    }

    private static string Translate(string outcome) => outcome switch
    {
        "Blocked" => "bloqueado",
        "NotApplicable" => "não se aplica",
        "FailedAndCompensated" => "falhou (nada mudou)",
        "Failed" => "falhou",
        "Interrupted" => "interrompido (veja Aplicar > Recuperar)",
        "SkippedExternalChange" => "mudou enquanto isso; nada feito",
        "AlreadyConfigured" => "já estava assim",
        _ => outcome,
    };
}
