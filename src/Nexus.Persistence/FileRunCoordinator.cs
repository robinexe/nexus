using System.Diagnostics;
using System.Text.Json;
using Nexus.Core;

namespace Nexus.Persistence;

/// <summary>
/// Coordenação entre interface, CLI e broker por uma trava exclusiva do sistema operacional sobre <c>nexus.lock</c>,
/// no mesmo diretório do diário.
/// <list type="bullet">
/// <item>Prova de vida = a trava do SO. Ela some quando o processo termina, inclusive por crash ou "Finalizar tarefa";
/// reutilização de PID não interfere.</item>
/// <item>O arquivo nunca é apagado: apagar no fechamento abre uma corrida em que dois processos travam inodes diferentes
/// (Unix) ou o segundo recebe "acesso negado" durante a exclusão pendente (Windows).</item>
/// <item><c>nexus.lock.owner.json</c> descreve o dono atual (PID, início do processo, usuário, execução). É só diagnóstico.
/// Se a trava está livre e o registro existe, o dono anterior morreu sem liberar: trava abandonada.</item>
/// <item>Sem permissão ou com erro de E/S a resposta é <see cref="LeaseAcquireStatus.AccessDenied"/>/<see cref="LeaseAcquireStatus.Error"/>
/// e quem chama deve recusar. Nunca se presume "livre".</item>
/// </list>
/// </summary>
public sealed class FileRunCoordinator : IRunCoordinator
{
    public const string LockFileName = "nexus.lock";
    public const string OwnerFileName = "nexus.lock.owner.json";
    public const int MaxOwnerRecordBytes = 4096;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _lockPath;
    private readonly string _ownerPath;

    public FileRunCoordinator(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _lockPath = Path.Combine(Directory, LockFileName);
        _ownerPath = Path.Combine(Directory, OwnerFileName);
    }

    /// <summary>Coordenador do diário em <paramref name="journalPath"/> (trava no mesmo diretório).</summary>
    public static FileRunCoordinator ForJournal(string journalPath) =>
        new(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);

    public string Directory { get; }

    public LeaseAcquireResult TryAcquire(string purpose, TimeSpan wait = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var (status, stream, detail) = OpenExclusive(FileMode.OpenOrCreate);
            if (status == LeaseAcquireStatus.Acquired)
            {
                var holder = CurrentProcessHolder(purpose);
                var lease = new FileRunLease(this, stream!, holder);
                lease.WriteOwnerRecord();
                return new LeaseAcquireResult(LeaseAcquireStatus.Acquired, lease, holder, null);
            }
            if (status != LeaseAcquireStatus.HeldByOther || DateTime.UtcNow >= deadline)
                return new LeaseAcquireResult(status, null, ReadOwnerRecord(), detail);
            Thread.Sleep(50);
        }
    }

    public CoordinationProbe Probe()
    {
        // Somente leitura: não cria o arquivo de trava. Sem arquivo, nenhum executor jamais segurou a trava aqui.
        if (!File.Exists(_lockPath))
        {
            var stale = ReadOwnerRecord();
            return new CoordinationProbe(CoordinationState.Idle, stale, null);
        }
        var (status, stream, detail) = OpenExclusive(FileMode.Open);
        var holder = ReadOwnerRecord();
        switch (status)
        {
            case LeaseAcquireStatus.Acquired:
                stream?.Dispose();
                return new CoordinationProbe(CoordinationState.Idle, holder,
                    holder is null ? null : "Trava livre, mas há registro de um dono anterior (processo encerrado sem liberar, ou falha ao apagar o registro).");
            case LeaseAcquireStatus.HeldByOther:
                // O registro só identifica o dono se o processo nele descrito estiver vivo (PID + horário de início).
                if (holder is not null && !IsLive(holder))
                    return new CoordinationProbe(CoordinationState.Active, null,
                        $"Executor ativo; o registro de dono está desatualizado (descreve o pid {holder.ProcessId}, que não corresponde a um processo vivo).");
                return new CoordinationProbe(CoordinationState.Active, holder,
                    holder is null ? "Executor ativo; registro do dono ainda não gravado ou ilegível." : null);
            default:
                return new CoordinationProbe(CoordinationState.Unknown, holder, detail);
        }
    }

    private (LeaseAcquireStatus Status, FileStream? Stream, string? Detail) OpenExclusive(FileMode mode)
    {
        try
        {
            if (mode != FileMode.Open) System.IO.Directory.CreateDirectory(Directory);
            // FileShare.None: no Windows, compartilhamento negado; no Unix, o .NET usa flock(LOCK_EX|LOCK_NB).
            // Acesso só de leitura: a exclusão vem do compartilhamento, não do direito de escrita. Assim um usuário padrão
            // consegue disputar a trava criada pelo broker (SYSTEM) sem precisar de escrita no arquivo.
            var fs = new FileStream(_lockPath, mode, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            return (LeaseAcquireStatus.Acquired, fs, null);
        }
        catch (FileNotFoundException)
        {
            return (LeaseAcquireStatus.Acquired, null, null); // só em Probe (FileMode.Open): arquivo sumiu = livre
        }
        catch (UnauthorizedAccessException ex)
        {
            return (LeaseAcquireStatus.AccessDenied, null, ex.Message);
        }
        catch (IOException ex) when (IsLockConflict(ex))
        {
            return (LeaseAcquireStatus.HeldByOther, null, ex.Message);
        }
        catch (IOException ex)
        {
            return (LeaseAcquireStatus.Error, null, ex.Message);
        }
    }

    /// <summary>O processo do registro existe e tem o mesmo horário de início (PID reutilizado não passa).</summary>
    public static bool IsLive(LeaseHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (!string.Equals(holder.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var p = Process.GetProcessById(holder.ProcessId);
            if (holder.ProcessStartedAt is not { } recorded) return true;
            var actual = new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (actual - recorded).Duration() < TimeSpan.FromSeconds(2);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsLockConflict(IOException ex)
    {
        if (!OperatingSystem.IsWindows())
            return true; // flock EWOULDBLOCK; o .NET não expõe um HResult portátil
        var code = ex.HResult & 0xFFFF;
        return code is 32 /* ERROR_SHARING_VIOLATION */ or 33 /* ERROR_LOCK_VIOLATION */;
    }

    public LeaseHolder? ReadOwnerRecord()
    {
        try
        {
            if (!File.Exists(_ownerPath)) return null;
            // Compartilhamento total: a leitura não pode bloquear a troca/remoção do registro pelo dono da trava.
            using var fs = new FileStream(_ownerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxOwnerRecordBytes) return null; // registro anômalo (qualquer usuário pode escrever aqui): ignora
            using var reader = new StreamReader(fs);
            return JsonSerializer.Deserialize<LeaseHolder>(reader.ReadToEnd(), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static LeaseHolder CurrentProcessHolder(string purpose)
    {
        using var self = Process.GetCurrentProcess();
        DateTimeOffset? started = null;
        try { started = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
        return new LeaseHolder(Guid.NewGuid(), purpose, null, Environment.ProcessId, started,
            Environment.MachineName, Environment.UserName, DateTimeOffset.UtcNow);
    }

    private sealed class FileRunLease : IRunLease
    {
        private readonly FileRunCoordinator _owner;
        private readonly FileStream _stream;
        private readonly Lock _gate = new();
        private bool _disposed;

        public FileRunLease(FileRunCoordinator owner, FileStream stream, LeaseHolder holder)
        {
            _owner = owner;
            _stream = stream;
            Holder = holder;
        }

        public LeaseHolder Holder { get; private set; }
        public bool IsHeld { get { lock (_gate) return !_disposed; } }
        public Guid? ActiveRunId => Holder.RunId;

        public void BindRun(Guid runId)
        {
            lock (_gate)
            {
                EnsureHeldLocked();
                if (Holder.RunId is { } current && current != runId)
                    throw new InvalidOperationException($"A trava já protege a execução {current}; encerre-a antes de iniciar outra.");
                Holder = Holder with { RunId = runId };
                WriteOwnerRecord();
            }
        }

        public void UnbindRun(Guid runId)
        {
            lock (_gate)
            {
                EnsureHeldLocked();
                if (Holder.RunId == runId)
                {
                    Holder = Holder with { RunId = null };
                    WriteOwnerRecord();
                }
            }
        }

        public void EnsureHeld()
        {
            lock (_gate) EnsureHeldLocked();
        }

        private void EnsureHeldLocked() =>
            ObjectDisposedException.ThrowIf(_disposed, typeof(IRunLease));

        /// <summary>Grava o registro do dono de forma atômica (arquivo temporário + troca). Falha aqui não invalida a trava.</summary>
        public void WriteOwnerRecord()
        {
            try
            {
                var tmp = _owner._ownerPath + $".{Environment.ProcessId}.tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Holder, Json));
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Move(tmp, _owner._ownerPath, overwrite: true); break; }
                    catch (IOException) when (attempt < 5) { Thread.Sleep(20); } // leitor concorrente no Windows
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Diagnóstico apenas; a exclusão mútua continua garantida pela trava do SO.
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                // Apaga o registro ENQUANTO ainda segura a trava: depois de liberar, poderia apagar o do próximo dono.
                try { File.Delete(_owner._ownerPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                _stream.Dispose();
            }
        }
    }
}
