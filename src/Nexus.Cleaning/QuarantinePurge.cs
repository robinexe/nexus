using System.Globalization;
using Nexus.Core;

namespace Nexus.Cleaning;

/// <summary>
/// Apaga DE VEZ os arquivos de um lote da quarentena (irreversível). Só o handler do aplicador usa (internal). Nunca segue
/// links: um link/junção dentro da quarentena tem só a própria entrada removida, nunca o destino. Os manifestos ficam, marcados
/// como "purged" (com bytes e data), para o histórico mostrar "esvaziada" e o desfazer da limpeza recusar com motivo claro.
/// </summary>
internal sealed class QuarantinePurger
{
    private readonly TimeProvider _clock;

    public QuarantinePurger(TimeProvider clock) => _clock = clock;

    /// <summary>Esvazia um lote. Devolve arquivos e bytes apagados. Arquivo que não sai = IOException no fim (o resto sai).</summary>
    public (long Files, long Bytes) Purge(string dataDirectory, string batch)
    {
        var batchDir = QuarantineStore.BatchDirectory(dataDirectory, batch);
        if (!Directory.Exists(batchDir)) return (0, 0);
        QuarantineMover.EnsureRealChain(dataDirectory, batchDir);
        if (!CleaningPaths.IsRealDirectory(batchDir)) throw new IOException("A pasta do lote é um link ou junção; recusado.");

        long files = 0, bytes = 0;
        var failures = new List<string>();
        foreach (var ruleDir in Directory.EnumerateFileSystemEntries(batchDir).ToList())
        {
            var attributes = File.GetAttributes(ruleDir);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { RemoveLinkEntry(ruleDir, attributes); continue; }
            if ((attributes & FileAttributes.Directory) == 0) continue; // arquivo solto na pasta do lote: não é da limpeza

            QuarantineManifest? manifest = null;
            try { manifest = QuarantineStore.ReadManifest(ruleDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
            if (manifest is not null && manifest.Status != QuarantineManifest.Purged)
            {
                manifest.Status = QuarantineManifest.Purging; // ANTES de apagar: o desfazer da limpeza passa a recusar
                QuarantineMover.WriteManifest(ruleDir, manifest);
            }

            long ruleFiles = 0, ruleBytes = 0;
            var filesDir = Path.Combine(ruleDir, QuarantineStore.FilesDirectory);
            if (Directory.Exists(filesDir) || File.Exists(filesDir))
                DeleteTree(filesDir, ref ruleFiles, ref ruleBytes, failures);
            files += ruleFiles;
            bytes += ruleBytes;

            if (manifest is not null)
            {
                if (failures.Count == 0)
                {
                    manifest.Status = QuarantineManifest.Purged;
                    foreach (var e in manifest.Entries.Where(e => e.State is QuarantineEntryState.Moved or QuarantineEntryState.RestoreFailed))
                        e.State = QuarantineEntryState.Purged;
                }
                manifest.PurgedUtc = _clock.GetUtcNow().UtcDateTime;
                manifest.PurgedBytes += ruleBytes;
                manifest.PurgedFiles += ruleFiles;
                QuarantineMover.WriteManifest(ruleDir, manifest);
            }
        }
        if (failures.Count > 0)
            throw new IOException($"{failures.Count} arquivo(s) da quarentena não puderam ser apagados (ex.: {failures[0]}). O resto foi apagado.");
        return (files, bytes);
    }

    /// <summary>Apaga uma árvore sem seguir links (a entrada do link é removida; o destino, nunca).</summary>
    private static void DeleteTree(string path, ref long files, ref long bytes, List<string> failures)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) { RemoveLinkEntry(path, attributes); return; }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            try
            {
                var size = new FileInfo(path).Length;
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
                files++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(Path.GetFileName(path) + ": " + ex.Message); }
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path).ToList())
            DeleteTree(child, ref files, ref bytes, failures);
        try { Directory.Delete(path, recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (failures.Count == 0) failures.Add(Path.GetFileName(path) + ": " + ex.Message); }
    }

    private static void RemoveLinkEntry(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false); // remove a junção/link, não o destino
        else File.Delete(path);
    }
}

/// <summary>
/// Handler "CleaningQuarantinePurge" (IRREVERSÍVEL, contrato de docs/modules/disk.md). Alvo:
/// <c>level</c> = <c>user</c> (quarentena da conta, aplicador do usuário) ou <c>machine</c> (quarentena do administrador,
/// aplicador elevado) e UM de <c>batch</c> (id do lote) ou <c>olderThanDays</c> (1–999: lotes com mais de N dias).
/// <list type="bullet">
/// <item>Detect: arquivos e bytes de fato presentes na quarentena desses lotes; 0 = <c>CleaningQuarantine:purged</c>.</item>
/// <item>Apply: apaga de vez (sem seguir links) e marca os manifestos como esvaziados.</item>
/// <item>Restore: sempre recusa (não há cópia).</item>
/// </list>
/// </summary>
public sealed class CleaningQuarantinePurgeHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "CleaningQuarantinePurge";
    public const string StateType = "CleaningQuarantine";
    public const string Purged = "purged";
    public const string Present = "present";
    public const string LevelUser = "user";
    public const string LevelMachine = "machine";
    public const int MaxDays = 999;

    private readonly string _dataDirectory;
    private readonly bool _otherAccount;
    private readonly bool _elevated;
    private readonly TimeProvider _clock;
    private readonly QuarantinePurger _purger;

    public CleaningQuarantinePurgeHandler(string dataDirectory, bool otherAccount, bool elevated, TimeProvider? clock = null)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _otherAccount = otherAccount;
        _elevated = elevated;
        _clock = clock ?? TimeProvider.System;
        _purger = new QuarantinePurger(_clock);
    }

    public string HandlerKind => Kind;

    private string? TargetProblem(ActionInstance action, out IReadOnlyList<string> batches)
    {
        batches = [];
        if (!action.Definition.Irreversible) return "Catálogo inválido: esvaziar a quarentena precisa estar marcado como irreversível.";
        var t = action.Target;
        if (!t.TryGetValue("level", out var level)) return "Alvo sem nível (user/machine).";
        switch (level)
        {
            case LevelUser:
                if (action.Definition.Privileges != PrivilegeLevel.User) return "Privilégio do catálogo diferente do nível.";
                if (_otherAccount) return "A quarentena da sua conta só é esvaziada pelo aplicador da sua própria conta (não pelo serviço).";
                break;
            case LevelMachine:
                if (action.Definition.Privileges != PrivilegeLevel.Admin) return "Privilégio do catálogo diferente do nível.";
                if (!_elevated && !_otherAccount) return "A quarentena do administrador só é esvaziada pelo aplicador elevado.";
                break;
            default:
                return "Nível inválido.";
        }
        if (t.ContainsKey(UserProfileScope.TargetKey)) return "Este pedido não usa perfil de usuário.";
        var hasBatch = t.TryGetValue("batch", out var batch);
        var hasDays = t.TryGetValue("olderThanDays", out var daysText);
        if (hasBatch == hasDays) return "Informe um lote ou uma idade, não os dois.";
        if (hasBatch)
        {
            if (!CleaningBatchId.IsValid(batch)) return "Lote de limpeza inválido.";
            batches = [batch!];
            return null;
        }
        if (!int.TryParse(daysText, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 1 || days > MaxDays)
            return $"Idade inválida: de 1 a {MaxDays} dias.";
        batches = QuarantineStore.ExpiredBatches(_dataDirectory, days, _clock.GetUtcNow().UtcDateTime).Select(b => b.BatchId).ToList();
        return null;
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, out var batches) is { } problem) return Applicability.Blocked(problem);
        if (action.Target.ContainsKey("batch") && !Directory.Exists(QuarantineStore.BatchDirectory(_dataDirectory, batches[0])))
            return Applicability.NotApplicable("Este lote não está na quarentena desta conta.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, out var batches) is { } problem) throw new InvalidOperationException(problem);
        long files = 0, bytes = 0;
        foreach (var b in batches)
        {
            var (f, by) = QuarantineStore.MeasureOnDisk(_dataDirectory, b);
            files += f;
            bytes += by;
        }
        var detail = new Dictionary<string, string>
        {
            ["files"] = files.ToString(CultureInfo.InvariantCulture),
            ["bytes"] = bytes.ToString(CultureInfo.InvariantCulture),
            ["batches"] = string.Join(",", batches.Take(64)),
        };
        return StateSnapshot.Of(StateType, files == 0 && bytes == 0 ? Purged : Present, ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, out var batches) is { } problem) throw new InvalidOperationException(problem + " Nada foi apagado.");
        if (!action.Desired.Exists || action.Desired.Value != Purged) throw new InvalidOperationException("Valor desejado inválido para esvaziar a quarentena.");
        var errors = new List<string>();
        foreach (var b in batches)
        {
            try { _purger.Purge(_dataDirectory, b); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{b}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new IOException(string.Join(" ", errors));
    }

    public void Restore(ActionInstance action, StateSnapshot original) =>
        throw new InvalidOperationException("Esvaziar a quarentena é irreversível: os arquivos foram apagados de vez. Nada foi escrito.");

    public string? CheckRestore(ActionInstance action, StateSnapshot original) =>
        "Esvaziar a quarentena é irreversível: os arquivos foram apagados de vez.";
}

/// <summary>Tamanho da Lixeira (itens e bytes).</summary>
public readonly record struct RecycleBinSize(long Items, long Bytes);

/// <summary>Lixeira do usuário (trocável nos testes).</summary>
public interface IRecycleBinBackend
{
    /// <summary>Tamanho da Lixeira de uma unidade (<c>"C:\"</c>) ou de todas (null). Null se não der para ler.</summary>
    RecycleBinSize? Query(string? rootPath);

    /// <summary>Esvazia sem confirmação, sem som e sem barra de progresso do Shell. Devolve o HRESULT.</summary>
    int Empty(string? rootPath);

    /// <summary>A unidade existe e está pronta.</summary>
    bool DriveReady(string rootPath);
}

/// <summary>
/// Handler "CleaningRecycleBin" (IRREVERSÍVEL). Alvo: <c>drive</c> = letra (A–Z) ou <c>all</c>. Esvazia a Lixeira do usuário
/// pela API do Shell (<c>SHEmptyRecycleBinW</c> com SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND). Só no aplicador da
/// própria conta: a Lixeira é por usuário, e o serviço/outra conta esvaziaria a de outra pessoa (recusado).
/// Detect: tamanho pela <c>SHQueryRecycleBinW</c>; verificado = 0 itens e 0 bytes.
/// </summary>
public sealed class CleaningRecycleBinHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "CleaningRecycleBin";
    public const string StateType = "RecycleBin";
    public const string Empty = "empty";
    public const string NotEmpty = "not-empty";

    private readonly IRecycleBinBackend? _bin;
    private readonly bool _otherAccount;

    /// <param name="bin">Null = indisponível (fora do Windows).</param>
    public CleaningRecycleBinHandler(IRecycleBinBackend? bin, bool otherAccount)
    {
        _bin = bin;
        _otherAccount = otherAccount;
    }

    public string HandlerKind => Kind;

    private string? TargetProblem(ActionInstance action, out string? root)
    {
        root = null;
        if (!action.Definition.Irreversible) return "Catálogo inválido: esvaziar a Lixeira precisa estar marcado como irreversível.";
        if (action.Definition.Privileges != PrivilegeLevel.User) return "Esvaziar a Lixeira é uma ação da sua conta (nível usuário).";
        if (_otherAccount || action.Target.ContainsKey(UserProfileScope.TargetKey))
            return "A Lixeira é de cada usuário: só o aplicador da sua própria conta a esvazia (nunca o serviço ou outra conta).";
        if (_bin is null) return "Lixeira indisponível neste sistema.";
        if (!action.Target.TryGetValue("drive", out var drive)) return "Alvo sem unidade.";
        if (drive == "all") return null;
        if (drive.Length != 1 || drive[0] is < 'A' or > 'Z') return "Unidade inválida.";
        root = drive + @":\";
        if (!_bin.DriveReady(root)) return $"A unidade {drive}: não está disponível.";
        return null;
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TargetProblem(action, out _) is { } problem ? Applicability.Blocked(problem) : Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, out var root) is { } problem) throw new InvalidOperationException(problem);
        var size = _bin!.Query(root) ?? throw new InvalidOperationException("Não foi possível ler o tamanho da Lixeira.");
        return StateSnapshot.Of(StateType, size.Items == 0 && size.Bytes == 0 ? Empty : NotEmpty, ValueOrigin.Explicit, new Dictionary<string, string>
        {
            ["items"] = size.Items.ToString(CultureInfo.InvariantCulture),
            ["bytes"] = size.Bytes.ToString(CultureInfo.InvariantCulture),
            ["drive"] = root ?? "all",
        });
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, out var root) is { } problem) throw new InvalidOperationException(problem + " Nada foi apagado.");
        if (!action.Desired.Exists || action.Desired.Value != Empty) throw new InvalidOperationException("Valor desejado inválido para a Lixeira.");
        var hr = _bin!.Empty(root);
        // Lixeira já vazia devolve erro em algumas versões; quem decide é a releitura (Detect) do executor.
        if (hr != 0 && _bin.Query(root) is { } after && (after.Items > 0 || after.Bytes > 0))
            throw new IOException($"O Windows não esvaziou a Lixeira (código 0x{hr:X8}).");
    }

    public void Restore(ActionInstance action, StateSnapshot original) =>
        throw new InvalidOperationException("Esvaziar a Lixeira é irreversível: não há cópia dos arquivos. Nada foi escrito.");

    public string? CheckRestore(ActionInstance action, StateSnapshot original) =>
        "Esvaziar a Lixeira é irreversível: não há cópia dos arquivos.";
}
