using System.Globalization;
using System.Text.Json;
using Nexus.Core;
using Nexus.SystemTools.Drivers;

namespace Nexus.DriverUpdates;

/// <summary>Ferramentas oficiais para guardar e reinstalar um pacote de driver (pnputil + newdev).</summary>
public interface IDriverPackageTool
{
    /// <summary>Nome original do INF (ex.: "nv_dispig.inf") de um pacote publicado (oemNN.inf), pelo pnputil /enum-drivers.</summary>
    string? OriginalInfName(string publishedName);

    /// <summary><c>pnputil /export-driver oemNN.inf pasta</c>.</summary>
    void Export(string publishedName, string directory);

    /// <summary><c>pnputil /add-driver arquivo.inf</c> (coloca o pacote no repositório de drivers).</summary>
    void Add(string infPath);

    /// <summary>
    /// Instala à força o INF nos dispositivos com o hardware ID (UpdateDriverForPlugAndPlayDevices com INSTALLFLAG_FORCE, a API
    /// oficial do "atualizar driver" do Gerenciador de Dispositivos). Necessário para VOLTAR a uma versão mais antiga: o Windows
    /// normalmente prefere a mais nova. Retorna se o Windows pediu reinício.
    /// </summary>
    bool ForceInstall(string hardwareId, string infPath);

    /// <summary>Pasta de INFs do Windows (%SystemRoot%\INF).</summary>
    string InboxInfDirectory { get; }
}

/// <summary>Último início do Windows (para saber se um reinício pedido pela instalação já aconteceu).</summary>
public interface IBootClock
{
    DateTime LastBootUtc { get; }
}

/// <summary>Manifesto gravado ANTES de instalar: o que era o driver, onde ficou o backup e o que o WUA respondeu.</summary>
public sealed record DriverUpdateManifest(
    string UpdateId,
    int Revision,
    string Title,
    string? HardwareId,
    string? Model,
    string OfferedVersion,
    IReadOnlyList<string> DeviceInstanceIds,
    RollbackSource Source,
    string? PreviousInf,
    string? PreviousOriginalInf,
    string? PreviousMatchingId,
    string? PreviousVersion,
    DateTime CreatedUtc,
    DateTime? InstalledUtc = null,
    bool RebootRequired = false);

/// <summary>
/// Instala UMA atualização de driver oferecida pelo Windows Update (alvo: UpdateID + revisão, vindos do usuário por regex
/// estrita). Antes de qualquer coisa re-busca no WUA e confere que o ID é de driver e ainda está disponível.
/// <list type="bullet">
/// <item>Detect: "instalada" se algum dispositivo da oferta usa a versão oferecida (ou se o WUA instalou e pediu um reinício que
/// ainda não aconteceu); senão "pendente".</item>
/// <item>Apply: exporta o driver atual (pnputil /export-driver) para DataDirectory\backup\drivers\wu-&lt;id&gt;, grava o manifesto, e
/// só então baixa e instala pelo WUA (UpdateDownloader/UpdateInstaller). Backup que falha = nada é instalado.</item>
/// <item>Restore: reinstala o pacote anterior do backup (pnputil /add-driver + instalação forçada no ID que ele casava) ou o INF
/// do Windows. Sem backup possível, a restauração é recusada com o motivo (a confirmação já avisa isso).</item>
/// </list>
/// </summary>
public sealed class DriverUpdateInstallHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "DriverUpdateInstall";
    public const string StateType = "DriverUpdate";
    public const string Installed = "instalada";
    public const string Pending = "pendente";
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    private readonly IWindowsUpdateAgent _wua;
    private readonly IDeviceInventory _devices;
    private readonly IDriverPackageTool _tool;
    private readonly IBootClock _clock;
    private readonly string _backupRoot;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _gate = new();
    private (DateTime At, WuSearchOutcome Outcome)? _cache;

    public DriverUpdateInstallHandler(IWindowsUpdateAgent wua, IDeviceInventory devices, IDriverPackageTool tool, IBootClock clock, string dataDirectory,
        Func<DateTime>? utcNow = null)
    {
        _wua = wua ?? throw new ArgumentNullException(nameof(wua));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _backupRoot = BackupRoot(dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory)));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string HandlerKind => Kind;

    /// <summary>Mesma raiz dos backups de drivers do módulo Ferramentas do sistema (DataDirectory\backup\drivers).</summary>
    public static string BackupRoot(string dataDirectory) => DriverPackageHandler.BackupRoot(dataDirectory);

    public string BackupDirectory(string updateId) => Path.Combine(_backupRoot, "wu-" + updateId);
    private string ManifestPath(string updateId) => Path.Combine(BackupDirectory(updateId), "nexus-manifest.json");

    private static (string? Id, int Revision) Ids(ActionInstance a)
    {
        var id = a.Target.TryGetValue("updateId", out var i) ? i : null;
        var rev = a.Target.TryGetValue("revision", out var r) && WindowsUpdateQueries.TryParseRevision(r, out var n) ? n : -1;
        return (WindowsUpdateQueries.IsValidUpdateId(id) ? id : null, rev);
    }

    // ------------------------------------------------------------------ busca (com cache curto: Plan e Execute pedem de novo)

    private WuSearchOutcome Search()
    {
        lock (_gate)
        {
            if (_cache is { } c && _utcNow() - c.At < CacheLifetime && c.Outcome.Ok) return c.Outcome;
            var outcome = _wua.SearchDrivers(SearchTimeout, CancellationToken.None);
            _cache = (_utcNow(), outcome);
            return outcome;
        }
    }

    private void Invalidate()
    {
        lock (_gate) _cache = null;
    }

    /// <summary>A oferta ainda disponível com exatamente este ID e revisão, ou o motivo de não estar.</summary>
    private (WuDriverUpdate? Update, Applicability? Why) Find(string id, int revision)
    {
        var s = Search();
        if (!s.Ok) return (null, Applicability.Blocked(s.Message ?? "Não foi possível consultar o Windows Update."));
        var same = s.Updates.Where(u => u.UpdateId == id).ToList();
        if (same.Count == 0)
            return (null, Applicability.NotApplicable("Esta atualização não está mais disponível no Windows Update (já instalada, retirada ou oculta). Procure de novo."));
        var u = same.FirstOrDefault(x => x.Revision == revision);
        if (u is null) return (null, Applicability.Blocked("A revisão desta atualização mudou no Windows Update. Procure de novo antes de instalar."));
        if (!u.IsDriver) return (null, Applicability.Blocked("O ID informado não é de uma atualização de driver: recusado."));
        return (u, null);
    }

    // ------------------------------------------------------------------ contrato

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var (id, rev) = Ids(action);
        if (id is null || rev < 0) return Applicability.Blocked("Identificador de atualização inválido (esperado UpdateID em GUID e revisão numérica).");
        if (action.Desired.Type != StateType || action.Desired.Value != Installed) return Applicability.Blocked("Valor desejado inválido.");
        var (u, why) = Find(id, rev);
        if (u is null) return why!;
        if (!u.EulaAccepted)
            return Applicability.Blocked("Esta atualização pede aceitar termos de licença. O NEXUS não aceita termos por você: instale por Configurações > Windows Update.");
        if (u.OfferedVersion is null)
            return Applicability.Blocked("Não deu para saber a versão oferecida (título sem versão): sem ela o NEXUS não confirma a instalação. Use Configurações > Windows Update.");
        if (DeviceMatching.Match(u, _devices.List()).Count == 0)
            return Applicability.NotApplicable("Nenhum dispositivo presente corresponde a esta atualização (aparelho desconectado?).");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var (id, rev) = Ids(action);
        if (id is null || rev < 0) throw new InvalidOperationException("Identificador de atualização inválido.");
        var manifest = ReadManifest(id);
        if (manifest is not null && manifest.Revision != rev) manifest = null;
        string offered;
        WuDriverUpdate? probe;
        if (manifest is not null)
        {
            offered = manifest.OfferedVersion;
            probe = new WuDriverUpdate(id, rev, manifest.Title, true, false, true, false, 0, null, manifest.HardwareId, null, manifest.Model, null, null, offered, [], null);
        }
        else
        {
            var (u, why) = Find(id, rev);
            if (u is null) throw new InvalidOperationException(why?.Reason ?? "Atualização indisponível.");
            offered = u.OfferedVersion ?? throw new InvalidOperationException("Versão oferecida desconhecida.");
            probe = u;
        }

        var all = _devices.List();
        var matched = DeviceMatching.Match(probe, all).ToList();
        if (manifest is not null)
            foreach (var d in all.Where(d => manifest.DeviceInstanceIds.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase)))
                if (!matched.Contains(d)) matched.Add(d);
        var versions = string.Join(", ", matched.Select(d => d.DriverVersion ?? "sem driver").Distinct(StringComparer.Ordinal));
        var detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["oferecida"] = offered, ["atual"] = versions.Length == 0 ? "sem dispositivo" : versions };
        if (matched.Any(d => DeviceMatching.SameVersion(d.DriverVersion, offered)))
            return StateSnapshot.Of(StateType, Installed, detail: detail);
        if (manifest is { InstalledUtc: { } at, RebootRequired: true } && _clock.LastBootUtc < at)
        {
            detail["reinicio"] = "pendente";
            return StateSnapshot.Of(StateType, Installed, detail: detail);
        }
        return StateSnapshot.Of(StateType, Pending, detail: detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var (id, rev) = Ids(action);
        if (id is null || rev < 0) throw new InvalidOperationException("Identificador de atualização inválido.");
        Invalidate(); // re-busca agora, imediatamente antes de baixar
        var (u, why) = Find(id, rev);
        if (u is null) throw new InvalidOperationException(why?.Reason ?? "Atualização indisponível.");
        if (!u.EulaAccepted || u.OfferedVersion is null) throw new InvalidOperationException("Atualização sem condições de instalação automática (termos ou versão).");
        var devices = DeviceMatching.Match(u, _devices.List());
        if (devices.Count == 0) throw new InvalidOperationException("Nenhum dispositivo presente para esta atualização.");
        var primary = devices.FirstOrDefault(d => d.InfName is not null) ?? devices[0];
        var plan = RollbackRules.Plan(primary);

        var dir = BackupDirectory(id);
        if (Directory.Exists(dir))
            Directory.Move(dir, dir + "-" + _utcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)); // nunca sobrescreve backup anterior
        Directory.CreateDirectory(dir);

        string? original = null;
        if (plan.Source == RollbackSource.Backup)
        {
            original = _tool.OriginalInfName(plan.InfName!) ?? throw new InvalidOperationException($"{plan.InfName} não aparece no repositório de drivers: nada foi instalado.");
            _tool.Export(plan.InfName!, dir);
            if (FindInf(dir, original) is null)
                throw new InvalidOperationException($"O backup do driver atual ({plan.InfName}) não foi criado: nada foi instalado.");
        }
        else if (plan.Source == RollbackSource.Inbox)
        {
            original = plan.InfName;
            if (!File.Exists(Path.Combine(_tool.InboxInfDirectory, plan.InfName!))) plan = plan with { Source = RollbackSource.None };
        }

        var manifest = new DriverUpdateManifest(id, rev, u.Title, u.HardwareId, u.Model, u.OfferedVersion, devices.Select(d => d.InstanceId).ToList(),
            plan.Source, plan.InfName, original, plan.MatchingDeviceId, plan.PreviousVersion, _utcNow());
        WriteManifest(manifest);

        var result = _wua.DownloadAndInstall(id, rev, InstallTimeout);
        Invalidate();
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Message ?? $"O Windows Update não instalou (resultado {result.ResultCode}, 0x{unchecked((uint)result.HResult):X8}).");
        WriteManifest(manifest with { InstalledUtc = _utcNow(), RebootRequired = result.RebootRequired });
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (original.Value != Pending) return;
        var (id, _) = Ids(action);
        if (id is null) throw new InvalidOperationException("Identificador de atualização inválido.");
        var m = ReadManifest(id) ?? throw new InvalidOperationException("Manifesto do backup não encontrado: não há como voltar ao driver anterior.");
        switch (m.Source)
        {
            case RollbackSource.Backup:
            {
                var inf = FindInf(BackupDirectory(id), m.PreviousOriginalInf ?? "")
                          ?? throw new InvalidOperationException($"{m.PreviousOriginalInf} não está no backup.");
                _tool.Add(inf);
                _tool.ForceInstall(m.PreviousMatchingId!, inf);
                break;
            }
            case RollbackSource.Inbox:
            {
                var inf = Path.Combine(_tool.InboxInfDirectory, m.PreviousOriginalInf!);
                if (!File.Exists(inf)) throw new InvalidOperationException($"{m.PreviousOriginalInf} não existe mais em {_tool.InboxInfDirectory}.");
                _tool.ForceInstall(m.PreviousMatchingId!, inf);
                break;
            }
            default:
                throw new InvalidOperationException(NoRollback);
        }
    }

    public const string NoRollback = "Não há como voltar ao driver anterior: antes da instalação não existia um pacote que pudesse ser guardado (dispositivo sem driver ou driver sem ID registrado). Use a Restauração do Sistema (ponto criado antes de instalar).";

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        var (id, rev) = Ids(action);
        if (id is null || rev < 0) return "Identificador de atualização inválido.";
        if (original.Type != StateType || (original.Value != Pending && original.Value != Installed)) return "Original inválido.";
        if (original.Value == Installed) return null;
        var m = ReadManifest(id);
        if (m is null || m.Revision != rev) return "Manifesto do backup não encontrado; nada foi escrito.";
        if (m.Source == RollbackSource.None) return NoRollback;
        if (string.IsNullOrWhiteSpace(m.PreviousMatchingId) || m.PreviousMatchingId.Any(char.IsControl)) return "ID do dispositivo ausente no manifesto.";
        if (m.Source == RollbackSource.Backup && (m.PreviousOriginalInf is null || FindInf(BackupDirectory(id), m.PreviousOriginalInf) is null))
            return "O pacote do driver anterior não está no backup; nada foi escrito.";
        if (m.Source == RollbackSource.Inbox && !RollbackRules.IsInboxInfName(m.PreviousOriginalInf)) return "INF do Windows inválido no manifesto.";
        return null;
    }

    // ------------------------------------------------------------------ manifesto

    public DriverUpdateManifest? ReadManifest(string updateId)
    {
        if (!WindowsUpdateQueries.IsValidUpdateId(updateId)) return null;
        var path = ManifestPath(updateId);
        if (!File.Exists(path)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<DriverUpdateManifest>(File.ReadAllBytes(path));
            return m is not null && m.UpdateId == updateId ? m : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>O INF original dentro da pasta de backup (sem seguir links/junções).</summary>
    public static string? FindInf(string dir, string originalName)
    {
        if (!Directory.Exists(dir) || string.IsNullOrWhiteSpace(originalName) || originalName.IndexOfAny(['\\', '/', '*', '?', ':']) >= 0 || originalName.Contains("..", StringComparison.Ordinal))
            return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive, MaxRecursionDepth = 3 };
        return Directory.EnumerateFiles(dir, originalName, options).FirstOrDefault();
    }

    private void WriteManifest(DriverUpdateManifest m)
    {
        var path = ManifestPath(m.UpdateId);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(m));
        File.Move(tmp, path, overwrite: true);
    }
}
