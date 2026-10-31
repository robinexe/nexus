using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Nexus.Core;

namespace Nexus.SystemTools.Drivers;

/// <summary>Repositório de drivers do Windows (via pnputil, a ferramenta oficial).</summary>
public interface IDriverStore
{
    IReadOnlyList<DriverPackage> List();

    /// <summary>Pacotes em uso (nome publicado → dispositivos), ou null se não deu para determinar.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>>? InUse();

    /// <summary><c>pnputil /export-driver oemNN.inf pasta</c>.</summary>
    void Export(string publishedName, string directory);

    /// <summary><c>pnputil /delete-driver oemNN.inf</c> (sem /force: o Windows recusa se algum dispositivo usa o pacote).</summary>
    void Delete(string publishedName);

    /// <summary><c>pnputil /add-driver arquivo.inf /install</c>.</summary>
    void Add(string infPath);

    /// <summary><c>pnputil /export-driver * pasta</c> (todos os drivers de terceiros).</summary>
    void ExportAll(string directory);
}

/// <summary>Manifesto do backup de um pacote removido (para reconhecê-lo depois de reinstalado com outro oemNN.inf).</summary>
internal sealed record DriverBackupManifest(string PublishedName, string OriginalName, string Provider, string? ClassGuid, string VersionText, DateTime CreatedUtc);

/// <summary>
/// Remove do repositório um pacote de driver de terceiros que NÃO está em uso, com backup. Detect: "presente"/"removido"
/// (reconhece o mesmo pacote reinstalado com outro nome publicado). Apply: exporta para DataDirectory\backup\drivers\oemNN,
/// confere o INF exportado e só então remove (sem /force). Restore: <c>pnputil /add-driver … /install</c> do backup.
/// </summary>
public sealed class DriverPackageHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "SystemDriverPackage";
    public const string StateType = "DriverPackage";
    private readonly IDriverStore _store;
    private readonly string _backupRoot;

    public DriverPackageHandler(IDriverStore store, string dataDirectory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _backupRoot = Path.Combine(dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory)), "backup", "drivers");
    }

    public string HandlerKind => Kind;

    public static string BackupRoot(string dataDirectory) => Path.Combine(dataDirectory, "backup", "drivers");

    private static string NameOf(ActionInstance a) => a.Target.TryGetValue("publishedName", out var n) ? n.ToLowerInvariant() : "";

    public string BackupDirectory(string publishedName) => Path.Combine(_backupRoot, Path.GetFileNameWithoutExtension(publishedName.ToLowerInvariant()));
    private string ManifestPath(string publishedName) => Path.Combine(BackupDirectory(publishedName), "nexus-manifest.json");

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var name = NameOf(action);
        if (!DriverParsers.IsOemInf(name)) return Applicability.Blocked("Nome publicado inválido (esperado oemNN.inf).");
        if (action.Desired.Type != StateType || action.Desired.Value != PresenceState.Removed) return Applicability.Blocked("Valor desejado inválido.");
        var all = _store.List();
        var pkg = all.FirstOrDefault(p => p.PublishedName == name);
        if (pkg is null) return Applicability.NotApplicable($"{name} não está no repositório de drivers.");
        return DriverRules.CheckRemoval(pkg, all, _store.InUse()) is { } why ? Applicability.Blocked(why) : Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var name = NameOf(action);
        var all = _store.List();
        if (all.Any(p => p.PublishedName == name)) return PresenceState.Of(StateType, true);
        // Reinstalado pelo NEXUS (ou por outro meio) com outro oemNN.inf: mesmo pacote, mesma versão.
        var manifest = ReadManifest(name);
        var back = manifest is not null && all.Any(p =>
            string.Equals(p.OriginalName, manifest.OriginalName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Provider, manifest.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.ClassGuid, manifest.ClassGuid, StringComparison.OrdinalIgnoreCase) &&
            p.VersionText == manifest.VersionText);
        return PresenceState.Of(StateType, back);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var name = NameOf(action);
        var all = _store.List();
        var pkg = all.FirstOrDefault(p => p.PublishedName == name) ?? throw new InvalidOperationException($"{name} não está no repositório.");
        if (DriverRules.CheckRemoval(pkg, all, _store.InUse()) is { } why) throw new InvalidOperationException(why);

        var dir = BackupDirectory(name);
        if (Directory.Exists(dir))
        {
            // Backup anterior do mesmo nome: preservado com sufixo (nunca sobrescrito).
            Directory.Move(dir, dir + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        }
        Directory.CreateDirectory(dir);
        _store.Export(name, dir);
        if (FindInf(dir, pkg.OriginalName) is null)
            throw new InvalidOperationException($"A exportação não produziu {pkg.OriginalName}: nada foi removido.");
        var manifest = new DriverBackupManifest(name, pkg.OriginalName, pkg.Provider, pkg.ClassGuid, pkg.VersionText, DateTime.UtcNow);
        Text.WriteAtomic(ManifestPath(name), JsonSerializer.SerializeToUtf8Bytes(manifest));
        _store.Delete(name);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (original.Value != PresenceState.Present) return;
        var name = NameOf(action);
        var manifest = ReadManifest(name) ?? throw new InvalidOperationException($"Backup de {name} não encontrado em {BackupDirectory(name)}.");
        var inf = FindInf(BackupDirectory(name), manifest.OriginalName) ?? throw new InvalidOperationException($"{manifest.OriginalName} não está no backup.");
        _store.Add(inf);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        var name = NameOf(action);
        if (!DriverParsers.IsOemInf(name)) return "Nome publicado inválido.";
        if (original.Type != StateType || (original.Value != PresenceState.Present && original.Value != PresenceState.Removed)) return "Original inválido.";
        if (original.Value == PresenceState.Present && ReadManifest(name) is null) return "Backup do driver não encontrado; nada foi escrito.";
        return null;
    }

    private DriverBackupManifest? ReadManifest(string name)
    {
        var path = ManifestPath(name);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DriverBackupManifest>(File.ReadAllBytes(path)); }
        catch (JsonException) { return null; }
    }

    /// <summary>O INF original dentro da pasta de backup (sem seguir links/junções).</summary>
    internal static string? FindInf(string dir, string originalName)
    {
        if (!Directory.Exists(dir) || string.IsNullOrWhiteSpace(originalName) || originalName.IndexOfAny(['\\', '/']) >= 0) return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive, MaxRecursionDepth = 3 };
        return Directory.EnumerateFiles(dir, originalName, options).FirstOrDefault();
    }
}

/// <summary>Exporta todos os drivers de terceiros para DataDirectory\backup\drivers\todos-AAAAMMDD-HHMMSS. Efêmero: nada a desfazer (os arquivos ficam).</summary>
public sealed class DriverBackupAllHandler(IDriverStore store, string dataDirectory, EphemeralMarker? marker = null) : EphemeralHandler(marker)
{
    public const string Kind = "SystemDriverBackup";
    public override string HandlerKind => Kind;

    protected override void Run(ActionInstance action)
    {
        var dir = Path.Combine(DriverPackageHandler.BackupRoot(dataDirectory), "todos-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        store.ExportAll(dir);
    }
}

[SupportedOSPlatform("windows")]
public sealed class PnpUtilDriverStore(IProcessRunner runner) : IDriverStore
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(60), Long = TimeSpan.FromMinutes(10);
    private static string PnpUtil => SystemPaths.Tool("pnputil.exe");

    public IReadOnlyList<DriverPackage> List()
    {
        var r = runner.Run(PnpUtil, ["/enum-drivers"], Short);
        if (r.ExitCode != 0) throw new InvalidOperationException($"pnputil /enum-drivers falhou (código {r.ExitCode}).");
        return DriverParsers.ParseEnumDrivers(r.Output);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>>? InUse()
    {
        try
        {
            var r = runner.Run(PnpUtil, ["/enum-devices"], Short);
            return DriverParsers.ParseDevicesInUse(r.Output, r.ExitCode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { return null; }
    }

    public void Export(string publishedName, string directory) => Expect(runner.Run(PnpUtil, ["/export-driver", publishedName, directory], Long), "exportar", 0);

    public void Delete(string publishedName) => Expect(runner.Run(PnpUtil, ["/delete-driver", publishedName], Short), "remover", 0);

    // 3010 = instalado, reinício recomendado.
    public void Add(string infPath) => Expect(runner.Run(PnpUtil, ["/add-driver", infPath, "/install"], Long), "reinstalar", 0, 3010);

    public void ExportAll(string directory) => Expect(runner.Run(PnpUtil, ["/export-driver", "*", directory], Long), "exportar todos", 0);

    private static void Expect(ProcessResult r, string what, params int[] ok)
    {
        if (!ok.Contains(r.ExitCode))
            throw new InvalidOperationException($"pnputil não conseguiu {what} (código {r.ExitCode}): {Last(r.Combined)}");
    }

    private static string Last(string s)
    {
        var lines = Text.Lines(s).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return string.Join(" ", lines.TakeLast(3));
    }
}
