namespace Nexus.Boot.Impact;

/// <summary>Para onde o tempo de boot está indo nos boots guardados pelo Windows.</summary>
public enum BootTrendDirection { Unknown, Faster, Slower, Stable }

/// <summary>Média dos últimos boots contra a dos anteriores (ms do evento 100, BootTime).</summary>
public sealed record BootTrend(BootTrendDirection Direction, double RecentAverageMs, int RecentCount, double PreviousAverageMs, int PreviousCount)
{
    public static BootTrend Unknown { get; } = new(BootTrendDirection.Unknown, double.NaN, 0, double.NaN, 0);
    public double DeltaMs => RecentAverageMs - PreviousAverageMs;
}

/// <summary>Boots antes e depois de uma mudança na inicialização feita pelo NEXUS (desligar itens).</summary>
public sealed record BootBeforeAfter(DateTimeOffset ChangeUtc, double BeforeAverageMs, int BeforeCount, double AfterAverageMs, int AfterCount)
{
    /// <summary>Ainda não houve boot depois da mudança: "reinicie para ver o efeito".</summary>
    public bool WaitingForBoot => AfterCount == 0;
    /// <summary>Negativo = ficou mais rápido.</summary>
    public double DeltaMs => AfterCount == 0 || BeforeCount == 0 ? double.NaN : AfterAverageMs - BeforeAverageMs;
}

/// <summary>Lógica pura de tendência e antes x depois do boot (testada). Ruído de boot é grande: tudo usa média e margem de 10%.</summary>
public static class BootTrends
{
    /// <summary>Mudança menor que isto (fração da média anterior) é "estável": boots variam bastante sozinhos (atualizações, discos).</summary>
    public const double StableFraction = 0.10;

    /// <summary>Últimos <paramref name="window"/> boots contra os <paramref name="window"/> anteriores (precisa de 2 em cada lado).</summary>
    public static BootTrend Trend(IReadOnlyList<BootRecord> boots, int window = 5)
    {
        ArgumentNullException.ThrowIfNull(boots);
        var ordered = boots.OrderBy(b => b.TimeUtc).ToList();
        var recent = ordered.TakeLast(window).ToList();
        var previous = ordered.Take(Math.Max(0, ordered.Count - recent.Count)).TakeLast(window).ToList();
        if (recent.Count < 2 || previous.Count < 2) return BootTrend.Unknown;
        var r = recent.Average(b => (double)b.TotalMs);
        var p = previous.Average(b => (double)b.TotalMs);
        var direction = Math.Abs(r - p) < p * StableFraction ? BootTrendDirection.Stable : r < p ? BootTrendDirection.Faster : BootTrendDirection.Slower;
        return new BootTrend(direction, r, recent.Count, p, previous.Count);
    }

    /// <summary>
    /// Até <paramref name="window"/> boots antes da mudança contra até <paramref name="window"/> boots depois dela. Null se não há
    /// nenhum boot antes (não dá para comparar). Sem boot depois: <see cref="BootBeforeAfter.WaitingForBoot"/>.
    /// </summary>
    public static BootBeforeAfter? BeforeAfter(IReadOnlyList<BootRecord> boots, DateTimeOffset changeUtc, int window = 5)
    {
        ArgumentNullException.ThrowIfNull(boots);
        var before = boots.Where(b => b.TimeUtc < changeUtc).OrderBy(b => b.TimeUtc).TakeLast(window).ToList();
        if (before.Count == 0) return null;
        var after = boots.Where(b => b.TimeUtc > changeUtc).OrderBy(b => b.TimeUtc).Take(window).ToList();
        return new BootBeforeAfter(changeUtc, before.Average(b => (double)b.TotalMs), before.Count,
            after.Count == 0 ? double.NaN : after.Average(b => (double)b.TotalMs), after.Count);
    }

    /// <summary>A mudança mais recente que tem boots antes dela (as mais novas primeiro); null se nenhuma serve.</summary>
    public static BootBeforeAfter? LatestComparison(IReadOnlyList<BootRecord> boots, IEnumerable<DateTimeOffset> changes, int window = 5)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var c in changes.OrderByDescending(c => c))
            if (BeforeAfter(boots, c, window) is { } r) return r;
        return null;
    }

    /// <summary>A diferença passa da margem de ruído (10% da média de antes)?</summary>
    public static bool IsMeaningful(BootBeforeAfter r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return !double.IsNaN(r.DeltaMs) && Math.Abs(r.DeltaMs) >= r.BeforeAverageMs * StableFraction;
    }
}
