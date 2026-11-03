namespace Nexus.SystemTools.Crash;

/// <summary>O que o NEXUS recomenda depois de ler os travamentos (a interface escreve as frases no idioma escolhido).</summary>
public enum CrashAdvice
{
    /// <summary>O driver de vídeo reiniciou ou o jogo caiu dentro do driver: atualizar (instalação limpa) o driver de vídeo.</summary>
    UpdateGpuDriver,
    /// <summary>Um jogo cai sempre do mesmo jeito, fora do driver: verificar os arquivos do jogo pela loja.</summary>
    VerifyGameFiles,
    /// <summary>Muitos reinícios do driver ou vários jogos caindo: tirar overclock (placa/memória) e conferir temperatura.</summary>
    CheckOverclockAndTemps,
    /// <summary>Corrupção de memória (c0000374) ou instrução inválida, ou vários jogos caindo: testar a memória RAM.</summary>
    TestMemory,
}

/// <summary>Um jogo que fechou sozinho no período, com o módulo e o código mais comuns.</summary>
public sealed record GameCrashCount(string Exe, int Count, string? TopModule, string? TopException, bool InGpuDriver, DateTimeOffset Last);

/// <summary>Resumo dos travamentos de um período (padrão: 7 dias).</summary>
public sealed record CrashDigest(
    TimeSpan Window,
    IReadOnlyList<GameCrashCount> Games,
    int GameHangs,
    int VideoResets,
    IReadOnlyList<CrashAdvice> Advice)
{
    public bool Nothing => Games.Count == 0 && GameHangs == 0 && VideoResets == 0;
    public int TotalGameCrashes => Games.Sum(g => g.Count);
}

/// <summary>Lógica pura do resumo ("O jogo X fechou 3 vezes esta semana; o driver de vídeo reiniciou 2 vezes → atualize o driver").</summary>
public static class CrashDigestBuilder
{
    /// <summary>Prefixos dos módulos dos drivers de vídeo (NVIDIA, AMD, Intel) no modo usuário.</summary>
    private static readonly string[] GpuModulePrefixes =
    [
        "nvwgf2um", "nvd3dum", "nvoglv", "nvlddmkm", "nvgpucomp", "nvcuda", "nvapi",
        "atidxx", "atiumd", "amdxx", "amdxc", "aticfx", "atio6axx", "amdvlk", "amdihk",
        "igd10", "igd12", "igxel", "igc64", "ig9icd", "ig7icd", "igdumd",
    ];

    /// <summary>Módulos genéricos do Windows: o erro "aparece" neles, mas a causa costuma estar em outro lugar.</summary>
    private static readonly string[] GenericModules = ["ntdll.dll", "kernelbase.dll", "ucrtbase.dll", "msvcrt.dll", "kernel32.dll", "unknown"];

    public static bool IsGpuDriverModule(string? module) =>
        module is { Length: > 0 } m && GpuModulePrefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsGenericModule(string? module) =>
        module is null || GenericModules.Contains(module.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reinícios do driver de vídeo: 4101 e LiveKernelEvent de vídeo costumam descrever o MESMO reinício. Junta os dois e conta uma
    /// vez cada agrupamento com até 3 minutos entre eles.
    /// </summary>
    public static int CountVideoResets(IEnumerable<DateTimeOffset> displayResets, IEnumerable<DateTimeOffset> liveKernelVideo)
    {
        var all = displayResets.Concat(liveKernelVideo).OrderBy(t => t).ToList();
        var count = 0;
        DateTimeOffset? last = null;
        foreach (var t in all)
        {
            if (last is null || t - last.Value > TimeSpan.FromMinutes(3)) count++;
            last = t;
        }
        return count;
    }

    public static CrashDigest Build(IEnumerable<AppCrash> crashes, IEnumerable<WerEvent> wer, IEnumerable<DisplayReset> display, Func<string, string?, bool> isGame,
        DateTimeOffset now, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(crashes);
        ArgumentNullException.ThrowIfNull(wer);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(isGame);
        var span = window ?? TimeSpan.FromDays(7);
        var from = now - span;
        var games = crashes.Where(c => c.TimeUtc >= from && c.TimeUtc <= now + TimeSpan.FromMinutes(5) && isGame(c.AppName, c.AppPath))
            .GroupBy(c => c.AppName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var module = g.Where(c => !string.IsNullOrWhiteSpace(c.ModuleName)).GroupBy(c => c.ModuleName!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
                var code = g.Where(c => c.ExceptionCode is not null).GroupBy(c => c.ExceptionCode!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
                return new GameCrashCount(g.First().AppName, g.Count(), module, code, g.Any(c => IsGpuDriverModule(c.ModuleName)), g.Max(c => c.TimeUtc));
            })
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.Last).ToList();
        var werList = wer.Where(w => w.TimeUtc >= from && w.TimeUtc <= now + TimeSpan.FromMinutes(5)).ToList();
        var hangs = werList.Count(w => CrashEvents.IsHang(w) && w.P1 is { } app && isGame(app, null));
        var resets = CountVideoResets(display.Where(d => d.TimeUtc >= from && d.TimeUtc <= now + TimeSpan.FromMinutes(5)).Select(d => d.TimeUtc),
            werList.Where(CrashEvents.IsVideoLiveKernel).Select(w => w.TimeUtc));
        return new CrashDigest(span, games, hangs, resets, Advise(games, resets));
    }

    public static IReadOnlyList<CrashAdvice> Advise(IReadOnlyList<GameCrashCount> games, int videoResets)
    {
        ArgumentNullException.ThrowIfNull(games);
        var advice = new List<CrashAdvice>();
        if (videoResets > 0 || games.Any(g => g.InGpuDriver)) advice.Add(CrashAdvice.UpdateGpuDriver);
        var several = games.Count(g => g.Count > 0) >= 2;
        var memoryCodes = games.Any(g => g.TopException is "c0000374" or "c000001d" or "c0000420");
        if (videoResets >= 3 || several || memoryCodes) advice.Add(CrashAdvice.CheckOverclockAndTemps);
        if (several || memoryCodes) advice.Add(CrashAdvice.TestMemory);
        if (games.Any(g => g.Count >= 2 && !g.InGpuDriver && !several)) advice.Add(CrashAdvice.VerifyGameFiles);
        return advice;
    }
}
