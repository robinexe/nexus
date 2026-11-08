using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Measurement;

/// <summary>Tipo de rotina de driver que o kernel executou: DPC (trabalho adiado) ou ISR (rotina de interrupção).</summary>
public enum KernelRoutineKind { Dpc, Isr }

/// <summary>
/// Um evento PerfInfo DPC/ISR do rastreamento do kernel (ETW): endereço da rotina e quanto ela durou. É o que o LatencyMon e o
/// xperf leem. <paramref name="DurationMicroseconds"/> = carimbo do evento (fim) − InitialTime (entrada), já em microssegundos.
/// </summary>
public readonly record struct KernelRoutineEvent(KernelRoutineKind Kind, ulong Routine, double DurationMicroseconds, int Processor);

/// <summary>
/// Um módulo carregado no kernel (driver .sys, ntoskrnl.exe, hal.dll). <paramref name="Size"/> = 0 quando só o endereço base é
/// conhecido (EnumDeviceDrivers não informa tamanho); os eventos Image_Load do próprio rastreamento trazem base e tamanho.
/// </summary>
public readonly record struct KernelModule(ulong Base, ulong Size, string FileName);

/// <summary>Soma por módulo: quantas chamadas, tempo total e a pior chamada isolada, para DPC e para ISR.</summary>
/// <param name="IsKernelCore">ntoskrnl.exe/hal.dll: o próprio Windows, não um driver que dê para trocar (ver <see cref="DriverTraceAnalyzer.IsKernelCore"/>).</param>
public sealed record DriverModuleSummary(
    string Module,
    int DpcCount,
    double DpcTotalMs,
    double DpcMaxUs,
    int IsrCount,
    double IsrTotalMs,
    double IsrMaxUs,
    bool IsKernelCore)
{
    /// <summary>Pior chamada isolada do módulo (DPC ou ISR), em µs.</summary>
    [JsonIgnore]
    public double WorstUs => Math.Max(DpcMaxUs, IsrMaxUs);

    /// <summary>Tempo total que o módulo segurou algum núcleo (DPC + ISR), em ms.</summary>
    [JsonIgnore]
    public double TotalMs => DpcTotalMs + IsrTotalMs;
}

/// <summary>
/// Resultado da investigação por rastreamento (administrador). <paramref name="Unresolved"/> = eventos cujo endereço não caiu em
/// nenhum módulo conhecido (ficam fora da lista, contados aqui por honestidade).
/// </summary>
public sealed record DriverTraceResult(
    DateTimeOffset TakenAt,
    double DurationSeconds,
    int EventCount,
    int Unresolved,
    bool? VirtualMachine,
    IReadOnlyList<DriverModuleSummary> Modules)
{
    /// <summary>Diagnóstico técnico da captura (para o log/CI; a interface não mostra): modo da sessão, callbacks, buffers perdidos.</summary>
    public string? Diagnostics { get; init; }

    /// <summary>
    /// Provável culpado: o driver (não kernel) com a pior chamada isolada ≥ <see cref="DriverTraceAnalyzer.CulpritMinUs"/> µs; abaixo disso,
    /// ninguém é apontado (todos se comportaram). Lógica pura; a interface só traduz.
    /// </summary>
    [JsonIgnore]
    public DriverModuleSummary? Culprit =>
        Modules.Where(m => !m.IsKernelCore).OrderByDescending(m => m.WorstUs).FirstOrDefault(m => m.WorstUs >= DriverTraceAnalyzer.CulpritMinUs);

    /// <summary>Os cinco módulos de pior chamada isolada (inclui o kernel, marcado), para a lista da tela.</summary>
    [JsonIgnore]
    public IReadOnlyList<DriverModuleSummary> Top5 => Modules.OrderByDescending(m => m.WorstUs).ThenByDescending(m => m.TotalMs).Take(5).ToList();
}

/// <summary>
/// Agregação pura dos eventos DPC/ISR por módulo (testável com eventos sintéticos). Endereço → módulo: o módulo de maior base
/// ≤ endereço; com tamanho conhecido, o endereço tem de caber nele; sem tamanho (EnumDeviceDrivers), vale a base mais próxima.
/// </summary>
public static class DriverTraceAnalyzer
{
    /// <summary>Abaixo disto a pior chamada é inofensiva e ninguém vira "culpado" (LatencyMon fala em ~1 ms como problema; 500 µs é margem).</summary>
    public const double CulpritMinUs = 500;
    /// <summary>Um DPC/ISR acima de 1 s é lixo (relógio, evento perdido): descartado.</summary>
    public const double MaxPlausibleUs = 1_000_000;

    /// <summary>O Windows em si: ntoskrnl (e variantes), hal. Não é "culpa" de driver: executa temporizadores e trabalho do sistema
    /// — às vezes em nome de um driver que pediu (KeSetTimer), o que o rastreamento não distingue.</summary>
    public static bool IsKernelCore(string fileName)
    {
        var n = Path.GetFileName(fileName ?? "");
        return n.StartsWith("ntoskrnl", StringComparison.OrdinalIgnoreCase) || n.StartsWith("ntkrnl", StringComparison.OrdinalIgnoreCase) ||
               n.StartsWith("ntkrla57", StringComparison.OrdinalIgnoreCase) || n.Equals("hal.dll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Só o nome do arquivo ("\SystemRoot\system32\drivers\ndis.sys" → "ndis.sys"), minúsculo.</summary>
    public static string ModuleName(string fileName)
    {
        var s = fileName ?? "";
        var cut = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        return (cut >= 0 ? s[(cut + 1)..] : s).Trim().ToLowerInvariant();
    }

    /// <summary>Módulos ordenados por base, sem repetição de base (o que tem tamanho vence o que não tem).</summary>
    public static IReadOnlyList<KernelModule> Normalize(IEnumerable<KernelModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return modules.Where(m => m.Base != 0 && !string.IsNullOrWhiteSpace(m.FileName))
            .GroupBy(m => m.Base).Select(g => g.OrderByDescending(m => m.Size).First())
            .OrderBy(m => m.Base).ToList();
    }

    /// <summary>Módulo que contém o endereço, ou null. <paramref name="sorted"/> vem de <see cref="Normalize"/>.</summary>
    public static KernelModule? Resolve(ulong address, IReadOnlyList<KernelModule> sorted)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        int lo = 0, hi = sorted.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid].Base <= address) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return null;
        var m = sorted[found];
        if (m.Size > 0) return address < m.Base + m.Size ? m : null;
        // Sem tamanho: aceita até a base do módulo seguinte, ou até 64 MB (nenhum driver é maior).
        var limit = found + 1 < sorted.Count ? sorted[found + 1].Base : m.Base + (64UL << 20);
        return address < limit ? m : null;
    }

    public static DriverTraceResult Aggregate(IEnumerable<KernelRoutineEvent> events, IEnumerable<KernelModule> modules, double durationSeconds,
        DateTimeOffset takenAt, bool? virtualMachine = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var sorted = Normalize(modules);
        var acc = new Dictionary<string, Acc>(StringComparer.Ordinal);
        int count = 0, unresolved = 0;
        foreach (var e in events)
        {
            if (!double.IsFinite(e.DurationMicroseconds) || e.DurationMicroseconds < 0 || e.DurationMicroseconds > MaxPlausibleUs) continue;
            count++;
            var m = Resolve(e.Routine, sorted);
            if (m is null) { unresolved++; continue; }
            var name = ModuleName(m.Value.FileName);
            if (!acc.TryGetValue(name, out var a)) acc[name] = a = new Acc();
            if (e.Kind == KernelRoutineKind.Dpc) { a.DpcCount++; a.DpcTotalUs += e.DurationMicroseconds; a.DpcMaxUs = Math.Max(a.DpcMaxUs, e.DurationMicroseconds); }
            else { a.IsrCount++; a.IsrTotalUs += e.DurationMicroseconds; a.IsrMaxUs = Math.Max(a.IsrMaxUs, e.DurationMicroseconds); }
        }
        var list = acc.Select(kv => new DriverModuleSummary(kv.Key, kv.Value.DpcCount, Math.Round(kv.Value.DpcTotalUs / 1000, 3), Math.Round(kv.Value.DpcMaxUs, 1),
                kv.Value.IsrCount, Math.Round(kv.Value.IsrTotalUs / 1000, 3), Math.Round(kv.Value.IsrMaxUs, 1), IsKernelCore(kv.Key)))
            .OrderByDescending(m => m.WorstUs).ThenByDescending(m => m.TotalMs).ThenBy(m => m.Module, StringComparer.Ordinal).ToList();
        return new DriverTraceResult(takenAt, Math.Round(durationSeconds, 1), count, unresolved, virtualMachine, list);
    }

    private sealed class Acc
    {
        public int DpcCount, IsrCount;
        public double DpcTotalUs, IsrTotalUs, DpcMaxUs, IsrMaxUs;
    }
}

/// <summary>
/// Arquivo que leva o resultado do aplicador (administrador) para a interface (usuário): <c>%LocalAppData%\NEXUS\driver-latency\ultimo.json</c>
/// do perfil de quem pediu. É o mesmo caminho que o PresentMon elevado usa para devolver quadros (arquivo no perfil); o protocolo do
/// aplicador devolve só o veredito curto de uma ação efêmera, não dados.
/// </summary>
public static class DriverTraceFile
{
    public const string Folder = "driver-latency";
    public const string Name = "ultimo.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string localAppData) => Path.Combine(localAppData, "NEXUS", Folder, Name);

    /// <summary>Escrita atômica (tmp + rename), como os outros arquivos do NEXUS no perfil.</summary>
    public static void Save(string path, DriverTraceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(result, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Null se não existe ou não é um resultado válido (nunca lança).</summary>
    public static DriverTraceResult? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<DriverTraceResult>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
}
