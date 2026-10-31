using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Nexus.SelfTest;

/// <summary>Resultado de uma verificação. Na tela e no relatório: OK, AVISO, FALHA, IGNORADO.</summary>
public enum CheckStatus { Ok, Warning, Failure, Skipped }

/// <summary>Opções do autoteste (vindas de <see cref="SelfTestArguments"/>).</summary>
public sealed record SelfTestOptions
{
    /// <summary>Tempo limite padrão de cada verificação (cada uma pode pedir mais).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Também procura drivers no Windows Update (lento; só leitura).</summary>
    public bool WithWindowsUpdate { get; init; }
    /// <summary>Também roda medições curtas de CPU e memória (o PC trabalha por alguns segundos).</summary>
    public bool WithBenchmarks { get; init; }
    /// <summary>Limite de arquivos da verificação rápida de "Verificar arquivos".</summary>
    public int ScannerMaxFiles { get; init; } = 300;
    /// <summary>Só os módulos com estes nomes (vazio = todos). Comparação sem maiúsculas.</summary>
    public IReadOnlyList<string> Only { get; init; } = [];
    /// <summary>Pasta do catálogo (padrão: "catalog" ao lado do executável).</summary>
    public string CatalogDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "catalog");
}

/// <summary>Verificação pedida para ser ignorada (ex.: "precisa de --with-benchmarks").</summary>
public sealed class CheckSkippedException(string reason) : Exception(reason);

/// <summary>Uma verificação: lê algo do sistema e anota contagens. Exceção = FALHA; <see cref="CheckContext.Warn"/> = AVISO.</summary>
public sealed record SelfTestCheck(string Module, string Name, Func<CheckContext, Task> Run)
{
    /// <summary>Só faz sentido no Windows (fora dele: IGNORADO).</summary>
    public bool WindowsOnly { get; init; } = true;
    /// <summary>Tempo limite próprio (null = o padrão das opções).</summary>
    public TimeSpan? Timeout { get; init; }

    public SelfTestCheck(string module, string name, Action<CheckContext> run) : this(module, name, c => { run(c); return Task.CompletedTask; }) { }
}

/// <summary>
/// O que a verificação pode fazer: anotar dados (só contagens, sim/não e textos curtos, sempre sanitizados) e avisos.
/// Depois do fim (ou do tempo limite) o contexto é fechado: escritas atrasadas de uma leitura presa são ignoradas.
/// </summary>
public sealed class CheckContext
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, object> _data = new(StringComparer.Ordinal);
    private readonly List<string> _keys = [];
    private readonly List<string> _warnings = [];
    private bool _closed;

    internal CheckContext(SelfTestOptions options, ConcurrentDictionary<string, object> shared, CancellationToken cancel)
    {
        Options = options;
        Shared = shared;
        Cancel = cancel;
    }

    public SelfTestOptions Options { get; }
    /// <summary>Dados compartilhados entre verificações (ex.: a leitura do PC usada pelo Automático).</summary>
    public ConcurrentDictionary<string, object> Shared { get; }
    /// <summary>Cancelado no tempo limite: leituras longas devem repassá-lo.</summary>
    public CancellationToken Cancel { get; }

    public T? Get<T>(string key) where T : class => Shared.TryGetValue(key, out var v) ? v as T : null;

    public void Count(string key, long value) => Put(key, value);
    public void Number(string key, double value) => Put(key, Math.Round(value, 2));
    public void Flag(string key, bool value) => Put(key, value);
    public void Flag(string key, bool? value) { if (value is { } v) Put(key, v); else Put(key, "desconhecido"); }

    /// <summary>Texto curto (sanitizado e cortado em 80 caracteres). Nunca nomes de redes, programas ou caminhos de usuário.</summary>
    public void Info(string key, string? value) => Put(key, Sanitizer.Clip(Sanitizer.Clean(value ?? "—"), 80));

    /// <summary>Motivo esperado de uma leitura parcial (ex.: "precisa de administrador"). Vira AVISO.</summary>
    public void Warn(string reason)
    {
        lock (_gate) { if (!_closed && _warnings.Count < 20) _warnings.Add(Sanitizer.Clip(Sanitizer.Clean(reason), 300)); }
    }

    /// <summary>Encerra como IGNORADO (ex.: opção não pedida).</summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void Skip(string reason) => throw new CheckSkippedException(reason);

    private void Put(string key, object value)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (!_data.ContainsKey(key)) _keys.Add(key);
            _data[key] = value;
        }
    }

    internal (IReadOnlyDictionary<string, object> Data, IReadOnlyList<string> Keys, IReadOnlyList<string> Warnings) Close()
    {
        lock (_gate)
        {
            _closed = true;
            return (new Dictionary<string, object>(_data, StringComparer.Ordinal), _keys.ToList(), _warnings.ToList());
        }
    }
}

public sealed record CheckResult(
    string Module,
    string Name,
    CheckStatus Status,
    double Seconds,
    string Summary,
    IReadOnlyList<string> Warnings,
    string? Reason,
    string? Error,
    IReadOnlyDictionary<string, object> Data)
{
    public string StatusText => SelfTestReport.StatusText(Status);
}

public sealed record SelfTestEnvironment(
    string Os,
    string Architecture,
    bool Windows,
    bool? Elevated,
    string NexusVersion,
    string Runtime,
    int LogicalProcessors)
{
    public static SelfTestEnvironment Current()
    {
        bool? elevated = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                elevated = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        }
        var version = typeof(SelfTestEnvironment).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";
        return new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), OperatingSystem.IsWindows(), elevated,
            version, RuntimeInformation.FrameworkDescription, Environment.ProcessorCount);
    }
}

/// <summary>Roda as verificações uma a uma, cada uma com tempo limite, e agrega o relatório.</summary>
public static class SelfTestRunner
{
    public static async Task<SelfTestReport> RunAsync(IReadOnlyList<SelfTestCheck> checks, SelfTestOptions options, Action<CheckResult>? onResult = null,
        bool? isWindows = null, SelfTestEnvironment? environment = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(options);
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var started = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var shared = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        var results = new List<CheckResult>();
        foreach (var check in checks)
        {
            if (options.Only.Count > 0 && !options.Only.Any(o => string.Equals(o, check.Module, StringComparison.OrdinalIgnoreCase))) continue;
            CheckResult r;
            if (check.WindowsOnly && !windows) r = Skipped(check, "não é Windows");
            else r = await RunOneAsync(check, options, shared, cancel).ConfigureAwait(false);
            results.Add(r);
            onResult?.Invoke(r);
        }
        total.Stop();
        return new SelfTestReport(started, total.Elapsed.TotalSeconds, environment ?? SelfTestEnvironment.Current(), options.WithWindowsUpdate, options.WithBenchmarks, results);
    }

    private static CheckResult Skipped(SelfTestCheck c, string reason) =>
        new(c.Module, c.Name, CheckStatus.Skipped, 0, "", [], reason, null, new Dictionary<string, object>());

    internal static async Task<CheckResult> RunOneAsync(SelfTestCheck check, SelfTestOptions options, ConcurrentDictionary<string, object> shared, CancellationToken cancel)
    {
        var timeout = check.Timeout ?? options.Timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var ctx = new CheckContext(options, shared, cts.Token);
        var sw = Stopwatch.StartNew();
        // Task.Run: uma leitura síncrona presa não segura o relógio do tempo limite.
        var work = Task.Run(() => check.Run(ctx), CancellationToken.None);
        using var delayCts = new CancellationTokenSource();
        var delay = Task.Delay(timeout, delayCts.Token);
        var first = await Task.WhenAny(work, delay).ConfigureAwait(false);
        string? reason = null, error = null;
        CheckStatus status;
        if (first != work)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            // A leitura presa continua em segundo plano; o erro dela, se vier, é observado e descartado.
            _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            status = CheckStatus.Failure;
            reason = $"tempo limite de {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s excedido (a leitura não terminou)";
        }
        else
        {
            await delayCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await work.ConfigureAwait(false);
                status = CheckStatus.Ok;
            }
            catch (CheckSkippedException ex)
            {
                status = CheckStatus.Skipped;
                reason = Sanitizer.Clean(ex.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                status = CheckStatus.Failure;
                reason = Sanitizer.Clip(Sanitizer.Clean($"{ex.GetType().Name}: {ex.Message}"), 400);
                error = Sanitizer.Clip(Sanitizer.Clean(ex.ToString()), 6000);
            }
        }
        sw.Stop();
        var (data, keys, warnings) = ctx.Close();
        if (status == CheckStatus.Ok && warnings.Count > 0) { status = CheckStatus.Warning; reason = warnings[0]; }
        return new CheckResult(check.Module, check.Name, status, Math.Round(sw.Elapsed.TotalSeconds, 2), Summarize(data, keys), warnings, reason, error,
            keys.ToDictionary(k => k, k => data[k], StringComparer.Ordinal));
    }

    internal static string Summarize(IReadOnlyDictionary<string, object> data, IReadOnlyList<string> keys) =>
        string.Join(" · ", keys.Select(k => $"{k}: {Format(data[k])}"));

    internal static string Format(object v) => v switch
    {
        bool b => b ? "sim" : "não",
        double d => d.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")),
        long l => l.ToString("N0", CultureInfo.GetCultureInfo("pt-BR")),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
