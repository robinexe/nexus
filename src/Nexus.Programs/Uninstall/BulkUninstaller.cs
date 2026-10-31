using System.Globalization;
using Nexus.Programs.Inventory;

namespace Nexus.Programs.Uninstall;

/// <summary>O desinstalador não abriu.</summary>
public sealed class UninstallStartException(string message, bool cancelledByUser, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Você recusou o aviso de administrador do Windows.</summary>
    public bool CancelledByUser { get; } = cancelledByUser;
}

/// <summary>Um desinstalador em execução. O NEXUS só espera: nunca fecha nem mata o processo.</summary>
public interface IRunningUninstaller : IDisposable
{
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Código de saída, ou null quando não deu para ler (processo elevado, relançado etc.).</summary>
    int? ExitCode { get; }
}

/// <summary>Abre o desinstalador do fornecedor (o real no Windows; falso nos testes).</summary>
public interface IUninstallRunner
{
    /// <exception cref="UninstallStartException">Não abriu (inclusive aviso do Windows recusado).</exception>
    IRunningUninstaller Start(UninstallCommand command);
}

/// <summary>Sinal de "reinício pendente" do Windows. Compara antes/depois do lote: só conta o que mudou.</summary>
public interface IRebootPendingProbe
{
    /// <summary>Impressão digital do estado (null se não deu para ler).</summary>
    string? Fingerprint();
}

public enum BulkItemState { Waiting, Running, WaitingForChild, RetryLater, Removed, Failed, Skipped }

/// <summary>Estado de um item (imutável; cada mudança gera um novo).</summary>
public sealed record BulkItemStatus(BulkQueueItem Item, BulkItemState State, string Detail = "", int? ExitCode = null, bool RebootRequired = false, int Attempts = 0)
{
    public bool Finished => State is BulkItemState.Removed or BulkItemState.Failed or BulkItemState.Skipped;

    public string StateText => State switch
    {
        BulkItemState.Waiting => "Na fila",
        BulkItemState.Running => "Desinstalando",
        BulkItemState.WaitingForChild => "Esperando o desinstalador terminar",
        BulkItemState.RetryLater => "Vai tentar de novo",
        BulkItemState.Removed => RebootRequired ? "Removido (reiniciar)" : "Removido",
        BulkItemState.Failed => "Falhou",
        _ => "Pulado",
    };
}

/// <summary>Foto da fila para a interface.</summary>
public sealed record BulkQueueSnapshot(IReadOnlyList<BulkItemStatus> Items, int? CurrentIndex, bool StopRequested)
{
    public BulkItemStatus? Current => CurrentIndex is { } i ? Items[i] : null;
    public int Total => Items.Count;
    public int Removed => Items.Count(i => i.State == BulkItemState.Removed);
    public int Failed => Items.Count(i => i.State == BulkItemState.Failed);
    public int Skipped => Items.Count(i => i.State == BulkItemState.Skipped);
    public int Pending => Items.Count(i => i.State is BulkItemState.Waiting or BulkItemState.RetryLater);
    public int Done => Removed + Failed + Skipped;
}

public sealed record BulkUninstallOptions
{
    /// <summary>Espera máxima pelo desinstalador com janela (você pode estar lendo as telas dele).</summary>
    public TimeSpan InteractiveTimeout { get; init; } = TimeSpan.FromMinutes(60);
    /// <summary>Espera máxima por um silencioso / MSI.</summary>
    public TimeSpan QuietTimeout { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Depois que o processo sai, quanto esperar a entrada sumir (desinstaladores que se relançam de %TEMP%).</summary>
    public TimeSpan ChildWait { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>O registro às vezes é atualizado logo depois de o processo sair.</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>1618 (outra instalação em andamento): espera antes de tentar de novo.</summary>
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxBusyRetries { get; init; } = 2;
}

/// <summary>
/// Executor da fila: UM desinstalador por vez (o Windows Installer só aceita uma instalação por vez — erro 1618 —, e dois
/// desinstaladores com janela ao mesmo tempo confundem). Para cada item: abre, espera o processo sair (com tempo limite e
/// "pular", sem nunca fechar o desinstalador), relê o registro; se a entrada ainda existe e o processo saiu bem, espera os
/// processos filhos (entrada sumir) até <see cref="BulkUninstallOptions.ChildWait"/>. Códigos: 0 = ok; 3010/1641 = ok e
/// reiniciar; 1618 = ocupado, volta para o fim da fila e tenta de novo depois; 1602/1223 = cancelado por você; outros = falha
/// (a menos que a entrada tenha sumido). "Parar depois do atual" deixa o atual terminar e pula o resto.
/// </summary>
public sealed class BulkUninstaller
{
    private readonly BulkItemStatus[] _items;
    private readonly IUninstallRunner _runner;
    private readonly Func<InstalledProgram, bool> _stillInstalled;
    private readonly IRebootPendingProbe? _reboot;
    private readonly BulkUninstallOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _clock;
    private readonly IProgress<BulkQueueSnapshot>? _progress;
    private readonly Lock _gate = new();
    private int? _current;
    private volatile bool _stop;
    private CancellationTokenSource? _skip;

    public BulkUninstaller(IReadOnlyList<BulkQueueItem> items, IUninstallRunner runner, Func<InstalledProgram, bool> stillInstalled,
        IRebootPendingProbe? reboot = null, BulkUninstallOptions? options = null, IProgress<BulkQueueSnapshot>? progress = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.Select(i => new BulkItemStatus(i, BulkItemState.Waiting)).ToArray();
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _stillInstalled = stillInstalled ?? throw new ArgumentNullException(nameof(stillInstalled));
        _reboot = reboot;
        _options = options ?? new BulkUninstallOptions();
        _progress = progress;
        _delay = delay ?? Task.Delay;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public BulkQueueSnapshot Snapshot()
    {
        lock (_gate) return new BulkQueueSnapshot([.. _items], _current, _stop);
    }

    /// <summary>Deixa o desinstalador atual terminar e não abre os próximos.</summary>
    public void RequestStopAfterCurrent()
    {
        _stop = true;
        Report();
    }

    /// <summary>Para de esperar o item atual (o desinstalador dele NÃO é fechado) e segue a fila.</summary>
    public void SkipCurrent()
    {
        lock (_gate) _skip?.Cancel();
    }

    public async Task<BulkSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var before = SafeFingerprint();
        var notBefore = new Dictionary<int, DateTime>();
        var pending = new List<int>(Enumerable.Range(0, _items.Length));
        Report();
        while (pending.Count > 0)
        {
            if (_stop || cancellationToken.IsCancellationRequested)
            {
                foreach (var i in pending) Update(i, s => s with { State = BulkItemState.Skipped, Detail = "Não começou: você parou a fila." });
                break;
            }
            // O primeiro pronto; se todos estão esperando para tentar de novo, espera o mais cedo.
            var now = _clock();
            var index = pending.FirstOrDefault(i => !notBefore.TryGetValue(i, out var t) || t <= now, -1);
            if (index < 0)
            {
                index = pending.MinBy(i => notBefore[i]);
                var wait = notBefore[index] - now;
                SetCurrent(index);
                using var skip = NewSkip(cancellationToken);
                try { await _delay(wait, skip.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) continue;
                    pending.Remove(index);
                    Update(index, s => s with { State = BulkItemState.Skipped, Detail = "Pulado por você antes de tentar de novo." });
                    continue;
                }
                finally { ClearSkip(); }
            }
            pending.Remove(index);
            var retry = await RunItemAsync(index, cancellationToken).ConfigureAwait(false);
            if (retry)
            {
                pending.Add(index);
                notBefore[index] = _clock() + _options.BusyRetryDelay;
            }
        }
        SetCurrent(null);
        var after = SafeFingerprint();
        var pendingReboot = before is not null && after is not null && !string.Equals(before, after, StringComparison.Ordinal);
        return BulkSummary.From(Snapshot().Items, pendingReboot);
    }

    /// <returns>true = devolver à fila (1618).</returns>
    private async Task<bool> RunItemAsync(int index, CancellationToken ct)
    {
        SetCurrent(index);
        var item = _items[index].Item;
        Update(index, s => s with { State = BulkItemState.Running, Detail = "Desinstalador aberto.", Attempts = s.Attempts + 1 });
        using var skip = NewSkip(ct);
        try
        {
            IRunningUninstaller running;
            try { running = _runner.Start(item.Command); }
            catch (UninstallStartException e)
            {
                Finish(index, e.CancelledByUser ? BulkItemState.Skipped : BulkItemState.Failed,
                    e.CancelledByUser ? "Você recusou o aviso de administrador do Windows." : $"Não abriu: {e.Message}");
                return false;
            }

            int? code;
            using (running)
            {
                var limit = item.Mode == UninstallMode.Interactive ? _options.InteractiveTimeout : _options.QuietTimeout;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(skip.Token);
                timeout.CancelAfter(limit);
                try { await running.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    Finish(index, BulkItemState.Skipped, skip.IsCancellationRequested
                        ? "Pulado por você. O desinstalador não foi fechado: termine-o por lá se ainda estiver aberto."
                        : string.Create(CultureInfo.InvariantCulture, $"Tempo esgotado ({(int)limit.TotalMinutes} min). O desinstalador não foi fechado; confira a janela dele."));
                    return false;
                }
                code = running.ExitCode;
            }

            if (code == UninstallExitCodes.Busy)
            {
                if (_items[index].Attempts <= _options.MaxBusyRetries)
                {
                    Update(index, s => s with { State = BulkItemState.RetryLater, ExitCode = code, Detail = "Outra instalação estava em andamento (1618). Tenta de novo no fim da fila." });
                    return true;
                }
                Finish(index, BulkItemState.Failed, UninstallExitCodes.Explain(code), code);
                return false;
            }

            var reboot = UninstallExitCodes.IsRebootRequired(code);
            await SafeDelay(_options.SettleDelay, skip.Token).ConfigureAwait(false);
            if (!StillInstalled(item.Program))
            {
                Finish(index, BulkItemState.Removed, reboot ? "Removido. O Windows pede reiniciar para concluir." : "Removido.", code, reboot);
                return false;
            }
            if (UninstallExitCodes.IsCancelled(code))
            {
                Finish(index, BulkItemState.Skipped, "Cancelado na janela do desinstalador.", code);
                return false;
            }
            if (code is not null && !UninstallExitCodes.IsSuccess(code))
            {
                Finish(index, BulkItemState.Failed, UninstallExitCodes.Explain(code), code);
                return false;
            }

            // Saiu "bem" mas o programa continua registrado: muitos desinstaladores se copiam para %TEMP% e continuam lá.
            Update(index, s => s with { State = BulkItemState.WaitingForChild, ExitCode = code,
                Detail = "O desinstalador continua em outra janela. Conclua por lá; a fila espera o programa sair da lista." });
            var deadline = _clock() + _options.ChildWait;
            while (_clock() < deadline)
            {
                try { await _delay(_options.PollInterval, skip.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    Finish(index, BulkItemState.Skipped, "Pulado por você. Se o desinstalador ainda estiver aberto, termine por lá.", code);
                    return false;
                }
                if (!StillInstalled(item.Program))
                {
                    Finish(index, BulkItemState.Removed, reboot ? "Removido. O Windows pede reiniciar para concluir." : "Removido.", code, reboot);
                    return false;
                }
            }
            Finish(index, BulkItemState.Skipped,
                string.Create(CultureInfo.InvariantCulture, $"Continua instalado depois de {(int)_options.ChildWait.TotalMinutes} min de espera. Se o desinstalador ainda estiver aberto, termine por lá e leia a lista de novo."), code);
            return false;
        }
        finally { ClearSkip(); }
    }

    private bool StillInstalled(InstalledProgram p)
    {
        try { return _stillInstalled(p); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return true; // sem leitura, não afirma que saiu
        }
    }

    private async Task SafeDelay(TimeSpan t, CancellationToken ct)
    {
        if (t <= TimeSpan.Zero) return;
        try { await _delay(t, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private string? SafeFingerprint()
    {
        try { return _reboot?.Fingerprint(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private CancellationTokenSource NewSkip(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _skip = cts;
        return cts;
    }

    private void ClearSkip()
    {
        lock (_gate) _skip = null;
    }

    private void SetCurrent(int? index)
    {
        lock (_gate) _current = index;
        Report();
    }

    private void Finish(int index, BulkItemState state, string detail, int? code = null, bool reboot = false) =>
        Update(index, s => s with { State = state, Detail = detail, ExitCode = code ?? s.ExitCode, RebootRequired = reboot });

    private void Update(int index, Func<BulkItemStatus, BulkItemStatus> change)
    {
        lock (_gate) _items[index] = change(_items[index]);
        Report();
    }

    private void Report() => _progress?.Report(Snapshot());
}

/// <summary>Códigos de saída que importam (Windows Installer e convenções seguidas pela maioria dos desinstaladores).</summary>
public static class UninstallExitCodes
{
    public const int Success = 0, UserCancelledMsi = 1602, Fatal = 1603, NotInstalled = 1605, Busy = 1618, RebootInitiated = 1641, RebootRequired = 3010, Cancelled = 1223;

    public static bool IsSuccess(int? code) => code is null or Success or RebootRequired or RebootInitiated;
    public static bool IsRebootRequired(int? code) => code is RebootRequired or RebootInitiated;
    public static bool IsCancelled(int? code) => code is UserCancelledMsi or Cancelled;

    public static string Explain(int? code) => code switch
    {
        null => "Sem código de saída.",
        Success => "Concluído (código 0).",
        UserCancelledMsi => "Cancelado na janela do desinstalador (1602).",
        Cancelled => "Cancelado (1223).",
        Fatal => "O Windows Installer falhou durante a remoção (1603). Tente de novo depois de reiniciar ou use Configurações > Aplicativos.",
        NotInstalled => "O Windows Installer diz que o produto não está instalado (1605), mas a entrada continua na lista. Use Configurações > Aplicativos.",
        Busy => "Outra instalação continuou em andamento (1618). Espere ela terminar (ou reinicie) e tente de novo.",
        1619 => "O pacote do Windows Installer não foi encontrado (1619). O instalador original pode ter sido apagado do cache.",
        1612 => "A fonte da instalação não está disponível (1612).",
        RebootRequired => "Concluído; reiniciar para terminar (3010).",
        RebootInitiated => "Concluído; o desinstalador pediu reinício (1641).",
        _ => string.Create(CultureInfo.InvariantCulture, $"O desinstalador terminou com o código {code} e o programa continua instalado."),
    };
}

/// <summary>Resumo do lote.</summary>
public sealed record BulkSummary(
    IReadOnlyList<BulkItemStatus> Removed,
    IReadOnlyList<BulkItemStatus> Failed,
    IReadOnlyList<BulkItemStatus> Skipped,
    long FreedBytes,
    int RemovedWithoutSize,
    bool RebootRequired,
    bool RebootFromCodes,
    bool RebootFromWindows)
{
    public static BulkSummary From(IReadOnlyList<BulkItemStatus> items, bool windowsPendingChanged)
    {
        ArgumentNullException.ThrowIfNull(items);
        var removed = items.Where(i => i.State == BulkItemState.Removed).ToList();
        var codes = removed.Any(i => i.RebootRequired);
        return new BulkSummary(
            removed,
            items.Where(i => i.State == BulkItemState.Failed).ToList(),
            items.Where(i => i.State is not (BulkItemState.Removed or BulkItemState.Failed)).ToList(),
            removed.Sum(i => i.Item.Program.SizeBytes ?? 0),
            removed.Count(i => i.Item.Program.SizeBytes is null),
            codes || windowsPendingChanged,
            codes,
            windowsPendingChanged);
    }

    public IReadOnlyList<InstalledProgram> RemovedPrograms => Removed.Select(r => r.Item.Program).ToList();

    public string Headline => string.Create(CultureInfo.GetCultureInfo("pt-BR"),
        $"{Removed.Count} removido(s) · {Failed.Count} com falha · {Skipped.Count} pulado(s)");

    public string FreedText => FreedBytes > 0
        ? $"~{ProgramInventory.FormatSize(FreedBytes)} liberados (estimativa declarada pelos instaladores{(RemovedWithoutSize > 0 ? $"; {RemovedWithoutSize} sem tamanho informado" : "")})."
        : Removed.Count > 0 ? "Espaço liberado: os instaladores não informaram o tamanho." : "Nada foi removido.";

    public string Text()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Headline).Append(". ").Append(FreedText);
        if (RebootRequired)
            sb.Append(RebootFromCodes ? " Reinicie o PC para concluir (um desinstalador pediu)." : " Reinicie o PC para concluir (o Windows marcou arquivos para trocar no reinício).");
        foreach (var f in Failed) sb.Append(CultureInfo.InvariantCulture, $"\n• {f.Item.Program.Name}: {f.Detail}");
        foreach (var s in Skipped) sb.Append(CultureInfo.InvariantCulture, $"\n• {s.Item.Program.Name} (pulado): {s.Detail}");
        return sb.ToString();
    }
}
