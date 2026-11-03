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
using UiTone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>Linha com uma ação reversível (remover com backup) e o "Desfazer" da execução que a aplicou.</summary>
public sealed class SystemActionRow : ObservableObject
{
    private string _status = "";
    private string _tone = "muted";
    private Guid? _runId;
    private bool _applied;
    private bool _isChecked;

    public required string Key { get; init; }
    /// <summary>Campo do alvo que identifica esta linha no diário (ex.: profileName), para casar resultados do lote.</summary>
    public string TargetField { get; init; } = "";
    /// <summary>Marcada para "Remover selecionados" (lote).</summary>
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public bool Matches(string? target) =>
        target is not null && target.Split(" | ").Contains(TargetField + "=" + Key, StringComparer.Ordinal);
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";
    /// <summary>Motivo para não oferecer a remoção (proteção), ou null.</summary>
    public string? Blocked { get; init; }
    public bool CanApply => Blocked is null && !_applied;
    /// <summary>Linha de um lote: confirmação curta usada na lista do lote.</summary>
    public string ShortTitle => Title.Length > 70 ? string.Concat(Title.AsSpan(0, 70), "…") : Title;
    public string Status { get => _status; set => Set(ref _status, value); }
    /// <summary>good, bad, trace, signal ou muted (cor do selo).</summary>
    public string Tone { get => _tone; set { if (Set(ref _tone, value)) OnPropertyChanged(nameof(ToneValue)); } }
    /// <summary><see cref="Tone"/> no design system (selo do item e cor do estado).</summary>
    public UiTone ToneValue => _tone switch { "good" => UiTone.Good, "bad" => UiTone.Bad, "trace" => UiTone.Warn, "signal" => UiTone.Info, _ => UiTone.Neutral };
    public bool HasBadge => Badge.Length > 0;
    public Guid? RunId { get => _runId; set { if (Set(ref _runId, value)) OnPropertyChanged(nameof(CanRevert)); } }
    public bool Applied { get => _applied; set { if (Set(ref _applied, value)) { if (value) IsChecked = false; OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(CanRevert)); } } }
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
    public override string Title => Loc.T("Tools.NavTitle");
    public override string Group => Loc.T("Nav.Sistema");
    public override string Subtitle => Loc.T("Tools.NavSubtitle");

    private const string DnsAction = "system.network.dns-flush@1", ArpAction = "system.network.arp-flush@1";
    private const string WifiAction = "system.wifi.profile-remove@1", DriverAction = "system.driver.package-remove@1";
    private const string DriverBackupAction = "system.driver.backup-all@1", RestoreAction = "system.restore.create-point@1";
    private const string AppxAction = "system.appx.remove-user@1";

    private bool _loaded;
    private string _dns = "—", _arp = "—", _wifiNote = "", _driverNote = "", _appxNote = "", _restoreNote = "", _protection = "", _driverBackup = "";
    private bool _onlyCandidates = true;
    private List<SystemActionRow> _allDrivers = [];
    private int _driverCandidates;
    private bool _reading;
    private double _dnsNumber = double.NaN, _arpNumber = double.NaN, _driverPackages = double.NaN, _restoreCount = double.NaN, _oldWifi = double.NaN;
    private UiTone _protectionTone = UiTone.Neutral;

    /// <summary>Rede para jogos (checagens do PC): ping/jitter/perda, Wi-Fi ou cabo e o teste de bufferbloat.</summary>
    public Nexus.Desktop.ViewModels.Simple.GameNetworkPanel GameNetwork { get; }

    public SystemToolsPage()
    {
        GameNetwork = new Nexus.Desktop.ViewModels.Simple.GameNetworkPanel(Nexus.Desktop.ViewModels.Simple.PcChecksCoordinator.Shared, () => Host);
        RefreshCommand = new RelayCommand(() => _ = RunAsync(LoadAsync), () => !Busy);
        FlushDnsCommand = new RelayCommand(() => _ = RunAsync(() => EphemeralAsync(Loc.T("Tools.LimparCacheDns2"),
            Loc.T("Tools.EsvaziarCacheDnsWindows") +
            Loc.T("Tools.AjudaQuandoSiteMudou") +
            Loc.T("Tools.RiscoNenhumConhecidoDesfazer"), DnsAction, BrokerLevel.User)), () => !Busy);
        FlushArpCommand = new RelayCommand(() => _ = RunAsync(() => EphemeralAsync(Loc.T("Tools.LimparCacheArp2"),
            Loc.T("Tools.EsvaziarTabelaArpEnderecos") +
            Loc.T("Tools.GanhoDesempenhoNenhumRisco") +
            Loc.T("Tools.ExigeAdministradorWindowsVai"), ArpAction, BrokerLevel.Admin)), () => !Busy);
        OpenNetworkCommand = new RelayCommand(OpenNetworkSettings);
        BackupDriversCommand = new RelayCommand(() => _ = RunAsync(() => EphemeralAsync(Loc.T("Tools.BackupDrivers"),
            Loc.T("Tools.CopiarTodosDriversTerceiros") +
            Loc.T("Tools.UtilAntesFormatarOu"), DriverBackupAction, BrokerLevel.Admin)), () => !Busy);
        OpenDriverBackupsCommand = new RelayCommand(OpenDriverBackups);
        CreateRestorePointCommand = new RelayCommand(() => _ = RunAsync(() => EphemeralAsync(Loc.T("Tools.CriarPontoRestauracao"),
            Loc.T("Tools.PedirWindowsPontoRestauracao") +
            Loc.T("Tools.ExigeProtecaoSistemaLigada"),
            RestoreAction, BrokerLevel.Admin)), () => !Busy);
        OpenRestoreCommand = new RelayCommand(OpenSystemRestore);
        OpenProtectionCommand = new RelayCommand(OpenSystemProtection);
        RemoveSelectedWifiCommand = new RelayCommand(() => _ = RunAsync(() => RemoveWifiAsync(Selected(WifiProfiles))), () => !Busy && Selected(WifiProfiles).Count > 0);
        RemoveSelectedDriversCommand = new RelayCommand(() => _ = RunAsync(() => RemoveDriversAsync(Selected(Drivers))), () => !Busy && Selected(Drivers).Count > 0);
        RemoveSelectedAppsCommand = new RelayCommand(() => _ = RunAsync(() => RemoveAppsAsync(Selected(Apps))), () => !Busy && Selected(Apps).Count > 0);
        UndoBatchCommand = new RelayCommand(() => _ = RunAsync(UndoBatchAsync), () => !Busy && _lastBatch is not null);
    }

    /// <summary>Máximo de itens por lote (limite de ações por pedido do aplicador).</summary>
    public const int MaxBatch = BrokerService.MaxActionsPerRequest;
    private (Guid Run, BrokerLevel Level, string Label)? _lastBatch;

    public ICommand RemoveSelectedWifiCommand { get; }
    public ICommand RemoveSelectedDriversCommand { get; }
    public ICommand RemoveSelectedAppsCommand { get; }
    public ICommand UndoBatchCommand { get; }
    public bool CanUndoBatch => _lastBatch is not null;
    public string UndoBatchLabel => _lastBatch is { } b ? Loc.F("Tools.DesfazerLoteN", b.Label) : Loc.T("Tools.DesfazerLote");

    private static List<SystemActionRow> Selected(IEnumerable<SystemActionRow> rows) => rows.Where(r => r.IsChecked && r.CanApply).ToList();

    private IEnumerable<SystemActionRow> AllRows() => WifiProfiles.Concat(_allDrivers).Concat(Apps);

    // ------------------------------------------------------------------ estado lido

    public string DnsEntries { get => _dns; private set => Set(ref _dns, value); }
    private string _dnsCaption = Loc.T("Tools.EntradasGuardadas");
    public string DnsCaption { get => _dnsCaption; private set => Set(ref _dnsCaption, value); }
    public string ArpEntries { get => _arp; private set => Set(ref _arp, value); }
    public ObservableCollection<SystemActionRow> WifiProfiles { get; } = [];
    public string WifiNote { get => _wifiNote; private set => Set(ref _wifiNote, value); }
    public ObservableCollection<SystemActionRow> Drivers { get; } = [];
    public string DriverNote { get => _driverNote; private set => Set(ref _driverNote, value); }
    public string DriverBackupInfo { get => _driverBackup; private set => Set(ref _driverBackup, value); }
    public int DriverCandidates { get => _driverCandidates; private set { if (Set(ref _driverCandidates, value)) { OnPropertyChanged(nameof(DriverCandidatesNumber)); OnPropertyChanged(nameof(FixableNumber)); } } }
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

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Lendo (esqueleto no destaque e nas listas).</summary>
    public bool IsLoading { get => _reading; private set => Set(ref _reading, value); }
    public double DnsNumber { get => _dnsNumber; private set => Set(ref _dnsNumber, value); }
    public double ArpNumber { get => _arpNumber; private set => Set(ref _arpNumber, value); }
    public double WifiNumber => WifiProfiles.Count;
    /// <summary>Redes salvas sem conexão há 6+ meses.</summary>
    public double OldWifiNumber { get => _oldWifi; private set => Set(ref _oldWifi, value); }
    public double DriverPackagesNumber { get => _driverPackages; private set => Set(ref _driverPackages, value); }
    public double DriverCandidatesNumber => DriverCandidates;
    public double AppsNumber => Apps.Count;
    public double RestorePointNumber { get => _restoreCount; private set => Set(ref _restoreCount, value); }
    /// <summary>O que dá para arrumar: versões antigas de driver sem uso + redes Wi-Fi esquecidas (NaN antes de ler).</summary>
    public double FixableNumber => double.IsNaN(OldWifiNumber) ? double.NaN : DriverCandidates + OldWifiNumber;
    /// <summary>Verde: Proteção do Sistema ligada; vermelho: desligada; neutro: não lida.</summary>
    public UiTone ProtectionTone { get => _protectionTone; private set => Set(ref _protectionTone, value); }
    public string ProtectionChip => ProtectionTone switch { UiTone.Good => Loc.T("Tools.ProtecaoSistemaLigada"), UiTone.Bad => Loc.T("Tools.ProtecaoSistemaDesligada"), _ => Loc.T("Tools.ProtecaoSistemaNaoLida") };
    public bool HasWifi => WifiProfiles.Count > 0;
    public bool HasApps => Apps.Count > 0;
    public bool HasRestorePoints => RestorePoints.Count > 0;

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
        Message = "";
        IsLoading = true;
        try { await LoadCoreAsync(); }
        finally { IsLoading = false; }
    }

    private async Task LoadCoreAsync()
    {
        var network = Task.Run(SystemToolsReader.Network);
        var wifi = Task.Run(SystemToolsReader.Wifi);
        var drivers = Task.Run(SystemToolsReader.Drivers);
        var apps = Task.Run(SystemToolsReader.Appx);
        var restore = Task.Run(SystemToolsReader.RestorePoints);
        var backups = Task.Run(() => SystemToolsReader.DriverBackups(AdminDataDirectory));

        var n = await network;
        DnsEntries = n.DnsEntries is null ? "—" : N(n.DnsEntries);
        DnsCaption = n.DnsEntries is null
            ? Loc.T("Tools.ContarEntradasExigeAdministrador")
            : Loc.T("Tools.EntradasGuardadas");
        ArpEntries = N(n.ArpEntries);
        DnsNumber = n.DnsEntries is { } dn ? dn : double.NaN;
        ArpNumber = n.ArpEntries is { } an ? an : double.NaN;
        FillWifi(await wifi);
        FillDriverReading(await drivers);
        var b = await backups;
        DriverBackupInfo = b.Count == 0 ? Loc.T("Tools.NenhumBackupDriversFeito") : Loc.F("Tools.NBackupSMais", b.Count, b[0]);
        FillApps(await apps);
        FillRestore(await restore);
        Message = Loc.F("Tools.LidoN", DateTime.Now);
        foreach (var name in new[] { nameof(WifiNumber), nameof(AppsNumber), nameof(HasWifi), nameof(HasApps), nameof(HasRestorePoints), nameof(FixableNumber) })
            OnPropertyChanged(name);
    }

    // ------------------------------------------------------------------ Wi-Fi

    private void FillWifi(WifiReading r)
    {
        WifiProfiles.Clear();
        foreach (var g in r.Profiles.GroupBy(p => p.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var p = g.First();
            var last = r.LastConnected.TryGetValue(p.Name, out var at) ? Loc.F("Tools.UltimaConexaoN", at) : Loc.T("Tools.UltimaConexaoDesconhecida");
            var scope = p.Scope switch { WifiProfileScope.GroupPolicy => "empresa", WifiProfileScope.CurrentUser => Loc.T("Tools.SoEsteUsuario"), _ => Loc.T("Tools.TodosUsuarios") };
            var old = r.LastConnected.TryGetValue(p.Name, out var when) && when < DateTime.Now.AddDays(-180);
            var row = new SystemActionRow
            {
                Key = p.Name, TargetField = "profileName", Title = p.Name, Badge = old ? Loc.T("Tools.SemUsoHa6") : "",
                Detail = $"{scope} · {last} · {string.Join(", ", g.Select(x => x.Interface).Distinct())}",
                Blocked = p.Removable ? null : Loc.T("Tools.DefinidoPelaEmpresaPolitica"),
            };
            row.ApplyCommand = new RelayCommand(() => _ = RunAsync(() => RemoveWifiAsync([row])), () => !Busy && row.CanApply);
            row.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertRowAsync(row, BrokerLevel.Admin)), () => !Busy && row.CanRevert);
            if (old) row.Tone = "trace";
            WifiProfiles.Add(row);
        }
        WifiNote = r.Profiles.Count == 0 ? r.Note ?? Loc.T("Tools.NenhumaRedeWiFi") : Loc.F("Tools.NRedeSSalva", r.Profiles.Select(p => p.Name).Distinct().Count());
        OldWifiNumber = WifiProfiles.Count(w => w.HasBadge);
    }

    private Task RemoveWifiAsync(List<SystemActionRow> rows) => ApplyBatchAsync(rows, Loc.T("Tools.RedeSWiFi"), Loc.T("Tools.RemoverRedeWiFi"),
        (rows.Count == 1 ? Loc.F("Tools.RemoverRedeWiFi2", rows[0].Title) : Loc.F("Tools.RemoverNRedesWi", rows.Count, List(rows))) +
        Loc.T("Tools.NexusGuardaAntesCopia") +
        Loc.T("Tools.PastaDadosAplicadorDepois") +
        Loc.T("Tools.GanhoDesempenhoNenhumRisco2") +
        Loc.T("Tools.DesfazerReimportaPerfisGuardados"),
        row => new ActionRequest(WifiAction, Arguments: new Dictionary<string, string> { ["wifiProfile"] = row.Key }), BrokerLevel.Admin);

    private static string List(List<SystemActionRow> rows) =>
        string.Join("\n", rows.Take(10).Select(r => "• " + r.ShortTitle)) + (rows.Count > 10 ? Loc.F("Sec.MaisN", rows.Count - 10) : "");

    // ------------------------------------------------------------------ drivers

    private void FillDriverReading(DriverReading r)
    {
        _allDrivers = [];
        if (r.Error is not null) { DriverNote = Loc.T("Tools.NaoFoiPossivelLer") + r.Error; DriverCandidates = 0; Drivers.Clear(); return; }
        foreach (var p in r.Packages.OrderBy(p => p.ClassName, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.OriginalName, StringComparer.OrdinalIgnoreCase).ThenByDescending(p => p.Version))
        {
            var blocked = DriverRules.CheckRemoval(p, r.Packages, r.InUse);
            var newer = DriverRules.NewerDuplicate(p, r.Packages);
            var inUse = r.InUse is not null && r.InUse.ContainsKey(p.PublishedName);
            var row = new SystemActionRow
            {
                Key = p.PublishedName,
                TargetField = "publishedName",
                Title = $"{p.Provider} · {p.OriginalName}",
                Detail = Loc.F("Tools.NVersaoNN", p.ClassName, p.VersionText, (p.Date is { } d ? $" de {d:dd/MM/yyyy}" : ""), p.PublishedName) +
                         (newer is not null ? Loc.F("Tools.HaVersaoMaisNova", newer.VersionText) : ""),
                Badge = inUse ? Loc.T("Mon.Uso") : newer is not null ? Loc.T("Tools.VersaoAntiga") : Loc.T("Tools.NaoUsado"),
                Blocked = blocked,
                Tag = newer is not null && blocked is null,
            };
            row.Tone = inUse ? "good" : newer is not null ? "trace" : "muted";
            row.ApplyCommand = new RelayCommand(() => _ = RunAsync(() => RemoveDriversAsync([row])), () => !Busy && row.CanApply);
            row.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertRowAsync(row, BrokerLevel.Admin)), () => !Busy && row.CanRevert);
            _allDrivers.Add(row);
        }
        DriverCandidates = _allDrivers.Count(x => x.Tag is true);
        DriverPackagesNumber = r.Packages.Count;
        DriverNote = r.InUse is null
            ? Loc.F("Tools.NPacotesTerceirosNao", r.Packages.Count)
            : Loc.F("Tools.NPacotesTerceirosRepositorio", r.Packages.Count, DriverCandidates);
        FillDrivers();
    }

    private void FillDrivers()
    {
        Drivers.Clear();
        foreach (var row in _allDrivers.Where(x => !OnlyCandidates || x.Tag is true || x.Applied)) Drivers.Add(row);
    }

    private Task RemoveDriversAsync(List<SystemActionRow> rows) => ApplyBatchAsync(rows, "driver(s)", Loc.T("Tools.RemoverDriverAntigo"),
        (rows.Count == 1
            ? Loc.F("Tools.RemoverRepositorioDriversPacote", rows[0].Key, rows[0].Title, rows[0].Detail)
            : Loc.F("Tools.RemoverNPacotesAntigos", rows.Count, string.Join("\n", rows.Take(10).Select(r => $"• {r.Key} · {r.ShortTitle}"))) +
              (rows.Count > 10 ? Loc.F("Sec.MaisN", rows.Count - 10) : "")) +
        Loc.T("Tools.AntesNexusExportaCada") +
        Loc.T("Tools.AlgumDispositivoUsarPacote") +
        Loc.T("Tools.RiscoBaixoAparelhoDesconectado") +
        Loc.T("Tools.ExigeAdministrador"),
        row => new ActionRequest(DriverAction, Arguments: new Dictionary<string, string> { ["driverInf"] = row.Key }), BrokerLevel.Admin);

    private void OpenDriverBackups()
    {
        var root = DriverPackageHandler.BackupRoot(AdminDataDirectory);
        if (!Directory.Exists(root)) { Message = Loc.T("Tools.AindaNaoHaBackups"); return; }
        try { VendorTools.OpenFolder(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Message = Loc.T("Clean.NaoFoiPossivelAbrir2") + ex.Message; }
    }

    // ------------------------------------------------------------------ apps do Windows

    private void FillApps(AppxReading r)
    {
        Apps.Clear();
        if (r.Error is not null) { AppxNote = Loc.T("Tools.NaoFoiPossivelLer2") + r.Error; return; }
        var provisioned = r.Provisioned?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedCount = 0;
        foreach (var p in r.Packages.OrderBy(p => AppxCatalog.Classify(p.Name).Class).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (cls, entry) = AppxCatalog.Classify(p.Name);
            if (cls == AppxClass.Protected) { protectedCount++; continue; }
            if (cls == AppxClass.Unknown || entry is null) continue;
            var blocked = AppxCatalog.CheckRemoval(p);
            var prov = provisioned is null ? "" : provisioned.Contains(p.Name) ? Loc.T("Tools.TambemProvisionadoFicaContas") : "";
            var row = new SystemActionRow
            {
                Key = p.Name, TargetField = "packageName", Title = entry.Title, Detail = $"{entry.Note} · {p.Name} {p.Version}{prov}",
                Badge = cls == AppxClass.Safe ? Loc.T("Tools.SeguroRemover") : Loc.T("Conv.Atencao"), Blocked = blocked, Tag = p.PackageFamilyName,
            };
            row.Tone = cls == AppxClass.Safe ? "good" : "trace";
            row.ApplyCommand = new RelayCommand(() => _ = RunAsync(() => RemoveAppsAsync([row])), () => !Busy && row.CanApply);
            row.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertRowAsync(row, BrokerLevel.User)), () => !Busy && row.CanRevert);
            row.ExtraLabel = Loc.T("Tools.AbrirStore");
            row.ExtraCommand = new RelayCommand(() => OpenStore(row));
            Apps.Add(row);
        }
        AppxNote = Loc.F("Tools.NAppSLista", Apps.Count, protectedCount) +
                   (provisioned is null ? Loc.T("Tools.ListaProvisionadosExigeAdministrador") : Loc.F("Tools.NPacoteSProvisionado", provisioned.Count));
    }

    private Task RemoveAppsAsync(List<SystemActionRow> rows)
    {
        var warn = rows.Where(r => AppxCatalog.Classify(r.Key).Class == AppxClass.Warn).ToList();
        var head = rows.Count == 1
            ? Loc.F("Tools.RemoverNNSeu", rows[0].Title, rows[0].Key, rows[0].Detail)
            : Loc.F("Tools.RemoverNAppsSeu", rows.Count, List(rows));
        return ApplyBatchAsync(rows, "app(s)", Loc.T("Tools.RemoverAppWindows"),
            head + "\n" +
            (warn.Count > 0 ? Loc.F("Tools.AtencaoNNMuita", string.Join(", ", warn.Take(5).Select(w => w.ShortTitle)), (warn.Count == 1 ? Loc.T("Tools.Util") : Loc.T("Tools.SaoUteis"))) : "") +
            Loc.T("Tools.GanhoEspacoMenosAtalhos") +
            Loc.T("Tools.DesfazerLimitadoNexusReinstala") +
            Loc.T("Tools.NaoEstiveremSeraPreciso") +
            Loc.T("Tools.PacoteProvisionadoContasNovas"),
            row => new ActionRequest(AppxAction, Arguments: new Dictionary<string, string> { ["appxName"] = row.Key }), BrokerLevel.User);
    }

    private void OpenStore(SystemActionRow row)
    {
        if (row.Tag is not string family || !AppxCatalog.IsValidFamilyName(family)) return;
        if (Host?.Confirm(Loc.T("Tools.AbrirMicrosoftStore"), Loc.F("Tools.AbrirPaginaNMicrosoft", row.Title)) != true) return;
        try { VendorTools.OpenStorePage(family); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException) { Message = Loc.T("Tools.NaoFoiPossivelAbrir") + ex.Message; }
    }

    // ------------------------------------------------------------------ pontos de restauração

    private void FillRestore(RestoreReading r)
    {
        RestorePoints.Clear();
        if (r.Points.Items is { } items)
            foreach (var p in items.Take(30))
                RestorePoints.Add(new RestorePointRow(p.Created is { } c ? c.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) : "?", p.Description, RestoreParsers.TypeName(p.Type)));
        RestoreNote = r.Points.Items is null
            ? (r.Points.Error ?? Loc.T("Tools.NaoFoiPossivelListar")) + Loc.T("Tools.AbraRestauracaoSistemaVer")
            : r.Points.Items.Count == 0 ? Loc.T("Tools.NenhumPontoRestauracaoPc") : Loc.F("Tools.NPontoSRestauracao", r.Points.Items.Count);
        RestorePointNumber = r.Points.Items is { } list ? list.Count : double.NaN;
        ProtectionTone = r.DisabledByPolicy ? UiTone.Bad : r.ProtectedDrives is null ? UiTone.Neutral : r.ProtectedDrives.Count == 0 ? UiTone.Bad : UiTone.Good;
        OnPropertyChanged(nameof(ProtectionChip));
        ProtectionStatus = r.DisabledByPolicy ? Loc.T("Tools.DesligadaPoliticaOrganizacao")
            : r.ProtectedDrives is null ? Loc.T("Tools.EstadoProtecaoSistemaNao")
            : r.ProtectedDrives.Count == 0 ? Loc.T("Tools.ProtecaoSistemaDesligadaTodas")
            : Loc.T("Tools.ProtecaoSistemaLigada2") + string.Join(", ", r.ProtectedDrives.Select(d => d + ":"));
    }

    private void OpenSystemRestore()
    {
        if (Host?.Confirm(Loc.T("Tools.RestauracaoSistema"), Loc.T("Tools.AbrirRestauracaoSistemaWindows")) != true) return;
        try { VendorTools.OpenSystemRestore(); }
        catch (System.ComponentModel.Win32Exception ex) { Message = Loc.T("Clean.NaoFoiPossivelAbrir") + ex.Message; }
    }

    private void OpenSystemProtection()
    {
        try { VendorTools.OpenSystemProtection(); }
        catch (System.ComponentModel.Win32Exception ex) { Message = Loc.T("Clean.NaoFoiPossivelAbrir") + ex.Message; }
    }

    private void OpenNetworkSettings()
    {
        if (Host?.Confirm(Loc.T("Tools.RedefinicaoRede"), Loc.T("Tools.AbrirConfiguracoesRedeWindows") +
                Loc.T("Tools.NexusNaoFazIsso")) != true) return;
        try { VendorTools.OpenNetworkSettings(); }
        catch (System.ComponentModel.Win32Exception ex) { Message = Loc.T("Clean.NaoFoiPossivelAbrir") + ex.Message; }
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
            "Verified" => Loc.F("Tools.NFeito", title),
            "" => r.Message,
            _ => Loc.F("Tools.NNaoConcluidoN", title, outcome, detail),
        };
        await LoadAsync();
    }

    /// <summary>
    /// Lote: a mesma ação uma vez por linha (alvos diferentes), num pedido só — uma confirmação, um aviso do Windows, uma execução
    /// no diário e um desfazer. Cada resultado volta com a posição do pedido (<see cref="ActionResultDto.Index"/>).
    /// </summary>
    private async Task ApplyBatchAsync(List<SystemActionRow> rows, string noun, string title, string confirmation,
        Func<SystemActionRow, ActionRequest> request, BrokerLevel level)
    {
        if (Host is null || rows.Count == 0) return;
        if (rows.Count > MaxBatch) { Message = Loc.F("Tools.EscolhaMaximoNItens", MaxBatch); return; }
        var r = await Host.ApplyAsync(rows.Count == 1 ? title : $"{title} ({rows.Count})", confirmation, rows.Select(request).ToList(), level);
        if (r is null) return;
        var results = r.Response?.Results ?? [];
        if (results.Count == 0)
        {
            foreach (var row in rows) { row.Status = r.Message; row.Tone = "bad"; }
            Message = r.Message;
            return;
        }
        var done = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var res = results.FirstOrDefault(x => x.Index == i) ?? (rows.Count == 1 ? results[0] : null);
            if (res is { Outcome: "Verified" })
            {
                row.RunId = r.Response?.RunId;
                row.Applied = true;
                row.Status = Loc.T("Tools.RemovidoBackupGuardado");
                row.Tone = "good";
                done++;
            }
            else
            {
                row.Status = res is null ? Loc.T("Tools.SemResultadoAplicador") : $"{Translate(res.Outcome)}: {res.Detail}";
                row.Tone = "bad";
            }
        }
        if (done > 0 && r.Response?.RunId is { } run)
        {
            _lastBatch = (run, level, $"{done} {noun}");
            OnPropertyChanged(nameof(CanUndoBatch));
            OnPropertyChanged(nameof(UndoBatchLabel));
        }
        Message = rows.Count == 1
            ? (done == 1 ? Loc.F("Tools.NRemovidoUseDesfazer", rows[0].Title) : rows[0].Status)
            : done == rows.Count ? Loc.F("Tools.NNRemovidoS", done, noun)
            : Loc.F("Tools.NNRemovidoS2", done, rows.Count);
    }

    /// <summary>Desfazer de uma linha: o diário é por execução, então desfaz o lote em que ela foi removida (dito no status).</summary>
    private async Task RevertRowAsync(SystemActionRow row, BrokerLevel level)
    {
        if (row.RunId is not { } run) return;
        await RevertRunAsync(run, level);
    }

    private async Task UndoBatchAsync()
    {
        if (_lastBatch is not { } b) return;
        await RevertRunAsync(b.Run, b.Level);
    }

    private async Task RevertRunAsync(Guid run, BrokerLevel level)
    {
        if (Host is null) return;
        var rows = AllRows().Where(x => x.RunId == run).ToList();
        var r = await Host.RevertAsync(run, level);
        if (r is null) return;
        var results = r.Response?.Results ?? [];
        var all = r.Response?.FullyRestored == true;
        foreach (var row in rows)
        {
            var res = results.FirstOrDefault(x => row.Matches(x.Target));
            var ok = all || res is { Outcome: "Reverted" or "AlreadyOriginal" or "NothingToRevert" };
            if (ok)
            {
                row.Applied = false;
                row.RunId = null;
                row.Status = "restaurado";
                row.Tone = "signal";
            }
            else
            {
                row.Status = Loc.F("Tools.NaoRestauradoN", res?.Detail ?? r.Message);
                row.Tone = "bad";
            }
        }
        if (all && _lastBatch?.Run == run)
        {
            _lastBatch = null;
            OnPropertyChanged(nameof(CanUndoBatch));
            OnPropertyChanged(nameof(UndoBatchLabel));
        }
        Message = all ? (rows.Count == 1 ? Loc.F("Tools.NRestaurado", rows[0].Title) : Loc.F("Tools.NItensRestaurados", rows.Count))
                      : Loc.T("Tools.DesfazerNaoTerminouVeja");
    }

    private static string Translate(string outcome) => outcome switch
    {
        "Blocked" => "bloqueado",
        "NotApplicable" => Loc.T("Drv.NaoAplica"),
        "FailedAndCompensated" => Loc.T("Drv.FalhouNadaMudou"),
        "Failed" => "falhou",
        "Interrupted" => Loc.T("Drv.InterrompidoVejaAplicarRecuperar"),
        "SkippedExternalChange" => Loc.T("Drv.MudouEnquantoIssoNada"),
        "AlreadyConfigured" => Loc.T("Asst.JaEstavaAssim"),
        _ => outcome,
    };
}
