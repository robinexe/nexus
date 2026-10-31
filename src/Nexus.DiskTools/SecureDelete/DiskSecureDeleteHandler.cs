using System.Globalization;
using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.DiskTools.SecureDelete;

/// <summary>
/// Exclusão segura de UM arquivo escolhido pelo usuário. Ação IRREVERSÍVEL (catálogo: <c>"irreversible": true</c>): o executor só
/// aplica com confirmação explícita, e Restore nunca é chamado (se for, recusa).
/// <para>Alvo: <c>path</c> = argumento do usuário <c>filePath</c>, validado pela regra do catálogo no broker e de novo aqui
/// (<see cref="SecureDeletePolicy"/>), mais as verificações no disco: só arquivo comum (nunca pasta, link simbólico, junção no
/// caminho, arquivo de nuvem, compactado/esparso pelo NTFS, somente leitura ou com vários links físicos), não em uso, até
/// <see cref="MaxBytes"/>.</para>
/// <para>Roda só com a conta de quem pede e sem elevação (aplicador sob demanda do usuário): assim o NEXUS só consegue apagar
/// o que o próprio usuário conseguiria apagar no Explorer. Como serviço (outra conta) ou elevado, recusa.</para>
/// <para>Estado: <c>FileState:absent</c> (desejado) ou <c>FileState:present;size=…;mtime=…</c>.</para>
/// </summary>
public sealed class DiskSecureDeleteHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "DiskSecureDelete";
    public const string StateType = "FileState";
    public const string AbsentValue = "absent";
    public const string PathKey = "path";
    public const long MaxBytes = 32L * 1024 * 1024 * 1024;

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private readonly HandlerEnvironment _environment;
    private readonly ISecureDeleteFileSystem _fs;
    private readonly Func<string, string?> _pathCheck;

    /// <param name="pathCheck">Validação de caminho (produção: <see cref="SecureDeletePolicy.Check"/>). Null = permitido.</param>
    public DiskSecureDeleteHandler(HandlerEnvironment environment, ISecureDeleteFileSystem fileSystem, Func<string, string?> pathCheck)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _pathCheck = pathCheck ?? throw new ArgumentNullException(nameof(pathCheck));
    }

    /// <summary>Handler de produção: política com a pasta do Windows e a pasta de dados do aplicador (diário) protegidas.</summary>
    public static DiskSecureDeleteHandler ForEnvironment(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows)) windows = @"C:\Windows";
        var policy = new SecureDeletePolicy(windows, [environment.DataDirectory]);
        return new DiskSecureDeleteHandler(environment, new SecureDeleteFileSystem(), policy.Check);
    }

    public string HandlerKind => Kind;

    private static string PathOf(ActionInstance action) =>
        action.Target.TryGetValue(PathKey, out var p) && !string.IsNullOrEmpty(p) ? p : throw new ArgumentException("Alvo sem 'path'.");

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!action.Definition.Irreversible)
            return Applicability.Blocked("Catálogo inválido: a exclusão segura precisa estar marcada como irreversível.");
        if (_environment.OtherAccount)
            return Applicability.Blocked("A exclusão segura só roda com a sua própria conta (aplicador sob demanda), nunca como serviço: assim o NEXUS só apaga o que você mesmo poderia apagar.");
        if (_environment.Elevated)
            return Applicability.Blocked("A exclusão segura não roda como administrador: use o aplicador no nível de usuário.");
        if (!action.Target.TryGetValue(PathKey, out var path) || string.IsNullOrEmpty(path))
            return Applicability.Blocked("Nenhum arquivo escolhido.");
        if (_pathCheck(path) is { } refused) return Applicability.Blocked(refused);
        try
        {
            return CheckOnDisk(path, checkInUse: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Applicability.Blocked($"Não foi possível verificar o arquivo: {ex.Message}");
        }
    }

    private Applicability CheckOnDisk(string path, bool checkInUse)
    {
        var f = _fs.Probe(path);
        if (f.ReparseAncestor is { } anc)
            return Applicability.Blocked($"Há um link ou junção no caminho ({anc}): o arquivo real pode estar em outro lugar. Escolha o arquivo pelo caminho real.");
        if (!f.Exists) return Applicability.NotApplicable("O arquivo não existe (já foi apagado ou movido).");
        if (f.IsDirectory) return Applicability.Blocked("Escolha um arquivo. Pastas inteiras não são aceitas pela exclusão segura.");
        if (f.IsLink || (f.Attributes & FileAttributes.ReparsePoint) != 0)
            return Applicability.Blocked("O item é um link (atalho do sistema de arquivos, junção ou arquivo de nuvem). Sobrescrevê-lo não apagaria o conteúdo real.");
        if ((f.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            return Applicability.Blocked("Arquivo só na nuvem (OneDrive): o conteúdo não está neste disco. Apague pelo OneDrive.");
        if ((f.Attributes & FileAttributes.Compressed) != 0)
            return Applicability.Blocked("Arquivo compactado pelo NTFS: a sobrescrita vai para outros setores e o conteúdo antigo pode continuar no disco. Descompacte antes (Propriedades → Avançados).");
        if ((f.Attributes & FileAttributes.SparseFile) != 0)
            return Applicability.Blocked("Arquivo esparso: a sobrescrita não garante atingir os setores antigos.");
        if ((f.Attributes & FileAttributes.ReadOnly) != 0)
            return Applicability.Blocked("Arquivo somente leitura. Desmarque \"Somente leitura\" nas propriedades se tiver certeza.");
        if ((f.Attributes & FileAttributes.System) != 0)
            return Applicability.Blocked("Arquivo marcado como do sistema.");
        if (f.Length > MaxBytes)
            return Applicability.Blocked($"Arquivo grande demais para a exclusão segura (máximo {MaxBytes / (1024L * 1024 * 1024)} GB).");
        if (checkInUse && _fs.IsInUse(path))
            return Applicability.Blocked("O arquivo está aberto em outro programa. Feche o programa e tente de novo.");
        if (checkInUse && _fs.CheckOpenHandle(path) is { } handleProblem)
            return Applicability.Blocked(handleProblem);
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var path = PathOf(action);
        var f = _fs.Probe(path);
        var detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = path };
        if (!f.Exists) return StateSnapshot.Of(StateType, AbsentValue, ValueOrigin.Absent, detail);
        if (f.IsDirectory) return StateSnapshot.Of(StateType, "directory", ValueOrigin.Explicit, detail);
        var ticks = f.LastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        return StateSnapshot.Of(StateType, $"present;size={f.Length.ToString(CultureInfo.InvariantCulture)};mtime={ticks}", ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var path = PathOf(action);
        // Revalida tudo imediatamente antes (o arquivo pode ter mudado desde o Assess).
        if (!action.Definition.Irreversible || _environment.OtherAccount || _environment.Elevated)
            throw new InvalidOperationException("Exclusão segura recusada pelo ambiente do aplicador. Nada foi alterado.");
        if (_pathCheck(path) is { } refused) throw new InvalidOperationException(refused + " Nada foi alterado.");
        var check = CheckOnDisk(path, checkInUse: false); // em uso: a abertura exclusiva do OverwriteAndDelete recusa sozinha
        if (check.Kind != ApplicabilityKind.Applicable) throw new InvalidOperationException((check.Reason ?? "Recusado.") + " Nada foi alterado.");
        _fs.OverwriteAndDelete(path);
    }

    public void Restore(ActionInstance action, StateSnapshot original) =>
        throw new InvalidOperationException("Exclusão segura é irreversível: não existe cópia do arquivo para restaurar. Nada foi escrito.");

    public string? CheckRestore(ActionInstance action, StateSnapshot original) =>
        "Exclusão segura é irreversível: não existe cópia do arquivo para restaurar.";
}
