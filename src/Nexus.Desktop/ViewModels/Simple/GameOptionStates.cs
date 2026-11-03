using System.Globalization;
using System.IO;
using Nexus.Broker;
using Nexus.GameProfiles.Fortnite;
using Nexus.Lab.Gpu;
using Nexus.Measurement;

namespace Nexus.Desktop.ViewModels.Simple;

// =====================================================================================================================
// "Em Jogos, ao fazer alguma função, não salva que já fez" (relato do responsável, 1.0.0-ci.24): a tela Jogos montava os
// botões de novo a cada abertura e nunca lia o estado real das opções por jogo, então tudo voltava a "Ligar". Aqui fica a
// lógica pura (testada em Linux com leitores falsos): o estado REAL de cada opção, lido do mesmo lugar que a ação escreve
// (registro, banco de perfis do driver NVIDIA, arquivo do jogo), e a execução do diário que a desfaz. Nada aqui escreve.
// =====================================================================================================================

/// <summary>Estado atual de uma opção: ligada (como a ação deixa), desligada, com outro valor, ou sem leitura.</summary>
public enum OptionState { Unknown, Off, On, Different }

/// <summary>Uma ação de uma execução do diário: chave (id@versão), alvo resolvido e se ainda vale (aplicada/verificada).</summary>
public sealed record JournalItem(string Key, IReadOnlyDictionary<string, string> Target, bool Active)
{
    /// <summary>Foi desfeita (o Histórico mostra a execução como "desfeita").</summary>
    public bool Reverted { get; init; }
}

/// <summary>Uma execução do diário (sua conta ou sistema), com as ações dela.</summary>
public sealed record JournalRun(Guid RunId, BrokerLevel Level, DateTimeOffset Started, IReadOnlyList<JournalItem> Items)
{
    public bool Active => Items.Any(i => i.Active);
}

/// <summary>Uma parte de uma opção: a ação e, quando é por jogo, o valor do alvo (nome do executável ou caminho).</summary>
public sealed record OptionPart(string Key, string? TargetValue = null)
{
    public bool Matches(JournalItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Key == Key && (TargetValue is null || item.Target.Values.Any(v => string.Equals(v, TargetValue, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Como desfazer uma opção que já está ligada.</summary>
public enum UndoKind
{
    /// <summary>Nada no diário (já estava assim, ou foi feito fora do NEXUS).</summary>
    None,
    /// <summary>Execuções que só mexeram nesta opção: "Desfazer" aqui mesmo.</summary>
    Runs,
    /// <summary>Feita junto com outras coisas: desfazer aqui desfaria as outras também; fica no Histórico.</summary>
    History,
}

/// <param name="Runs">Execuções que desfazem, da mais antiga para a mais nova (o desfazer volta da mais nova para a mais antiga).</param>
public sealed record OptionUndo(UndoKind Kind, IReadOnlyList<(Guid Run, BrokerLevel Level)> Runs)
{
    public static readonly OptionUndo None = new(UndoKind.None, []);
}

/// <summary>Acha no diário a execução que ligou uma opção (por chave da ação + alvo do jogo).</summary>
public static class OptionJournal
{
    public static OptionUndo Find(IReadOnlyList<JournalRun> runs, IReadOnlyList<OptionPart> parts)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) return OptionUndo.None;
        bool Mine(JournalItem i) => parts.Any(p => p.Matches(i));
        var mine = runs.Where(r => r.Items.Any(i => i.Active && Mine(i))).OrderBy(r => r.Started).ToList();
        if (mine.Count == 0) return OptionUndo.None;
        // Só desfaz daqui quando cada execução mexeu apenas nesta opção (nada de desfazer outras coisas junto).
        return mine.All(r => r.Items.Where(i => i.Active).All(Mine))
            ? new OptionUndo(UndoKind.Runs, [.. mine.Select(r => (r.RunId, r.Level))])
            : new OptionUndo(UndoKind.History, []);
    }
}

/// <summary>De onde vem o estado de um jogo (no Windows: registro, NVAPI e arquivo do jogo; nos testes: falsos).</summary>
public interface IGameStateSource
{
    /// <summary>Leitor do banco de perfis do driver NVIDIA (só leitura).</summary>
    INvidiaDrsReader Nvidia { get; }
    /// <summary>Token SwapEffectUpgradeEnable do jogo em UserGpuPreferences (null = ausente).</summary>
    int? WindowedToken(string exePath);
    /// <summary>GameUserSettings.ini do Fortnite.</summary>
    FortniteState Fortnite();
}

/// <summary>O estado real de cada opção da tela Jogos, a partir do que a ação escreve.</summary>
public static class GameOptionStates
{
    public const string GameModeAction = "win.gamemode.enable@1";
    public const string WindowedToken = "SwapEffectUpgradeEnable";

    /// <summary>
    /// Modo de jogo: a releitura do catálogo depois de aplicar ("same" = valor 1 no registro) vale mais que o diagnóstico (lido ao
    /// abrir ou ao reler o PC); senão, o diagnóstico (ligado = valor 1 ou ausente, padrão do Windows; desligado = 0).
    /// </summary>
    public static OptionState GameMode(bool? fromDiagnosis, string? catalogReadState) =>
        catalogReadState switch
        {
            "same" => OptionState.On,
            "change" when fromDiagnosis == true => OptionState.On, // ausente: ligado pelo padrão do Windows (a ação gravaria 1)
            "change" => OptionState.Off,
            _ => fromDiagnosis switch { true => OptionState.On, false => OptionState.Off, _ => OptionState.Unknown },
        };

    /// <summary>Valor que vale para o perfil (o do perfil, ou o herdado); null quando o jogo não tem perfil (vale o padrão).</summary>
    public static uint? NvValue(DrsReading r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return !r.ProfileFound ? null : r.InProfile ? r.Value : r.Effective;
    }

    /// <summary>Um ajuste do driver: igual ao que a ação grava = ligado; padrão/ausente = desligado; outro = valor diferente.</summary>
    public static OptionState Nv(DrsReading? r, uint desired, uint defaultValue)
    {
        if (r is null) return OptionState.Unknown;
        var v = NvValue(r);
        return v == desired ? OptionState.On : v is null || v == defaultValue ? OptionState.Off : OptionState.Different;
    }

    /// <summary>
    /// Executáveis que recebem os ajustes do driver: o da biblioteca e, quando ele só abre o jogo (lançador/anticheat), os que
    /// desenham por ele (<see cref="GameAliases"/>): o driver aplica o perfil ao processo que desenha.
    /// </summary>
    public static IReadOnlyList<string> NvTargets(string exe)
    {
        var list = new List<string>();
        if (NvidiaDrsSettings.IsValidExe(exe)) list.Add(exe);
        if (GameAliases.IsBootstrapper(exe))
        {
            var render = GameAliases.RenderCandidates(exe).ToList();
            // O palpite genérico do Unreal (Nome-Win64-Shipping.exe) vem por último: só vale quando não há nome conhecido.
            var guess = Path.GetFileNameWithoutExtension(exe) + "-Win64-Shipping.exe";
            if (render.Count > 1 && string.Equals(render[^1], guess, StringComparison.OrdinalIgnoreCase)) render.RemoveAt(render.Count - 1);
            list.AddRange(render.Where(NvidiaDrsSettings.IsValidExe));
        }
        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Junta o estado de vários executáveis: ligado só quando todos estão; um diferente pesa mais que um desligado.</summary>
    public static OptionState Combine(IEnumerable<OptionState> states)
    {
        var list = states.ToList();
        if (list.Count == 0) return OptionState.Unknown;
        if (list.All(s => s == OptionState.On)) return OptionState.On;
        if (list.Contains(OptionState.Different)) return OptionState.Different;
        return list.Contains(OptionState.Off) ? OptionState.Off : OptionState.Unknown;
    }

    /// <summary>Lê um ajuste do driver em cada executável (falha de leitura = sem leitura, nunca "desligado").</summary>
    public static OptionState ReadNv(INvidiaDrsReader drs, IReadOnlyList<string> exes, uint settingId, uint desired, uint defaultValue)
    {
        ArgumentNullException.ThrowIfNull(drs);
        ArgumentNullException.ThrowIfNull(exes);
        return Combine(exes.Select(exe =>
        {
            try { return Nv(drs.Read(DrsTarget.App(exe), settingId), desired, defaultValue); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return OptionState.Unknown; }
        }));
    }

    public static OptionState NvLowLatency(INvidiaDrsReader drs, IReadOnlyList<string> exes) =>
        ReadNv(drs, exes, NvidiaDrsSettings.PrerenderLimit, NvidiaDrsSettings.LowLatencyOn, 0);

    public static OptionState NvMaxPower(INvidiaDrsReader drs, IReadOnlyList<string> exes) =>
        ReadNv(drs, exes, NvidiaDrsSettings.PreferredPstate, NvidiaDrsSettings.PstatePreferMax, NvidiaDrsSettings.PstateOptimalPower);

    public static OptionState NvFpsCap(INvidiaDrsReader drs, IReadOnlyList<string> exes, string fps) =>
        uint.TryParse(fps, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? ReadNv(drs, exes, NvidiaDrsSettings.FrameRateLimit, v, 0)
            : OptionState.Unknown;

    /// <summary>Otimizações para jogos em janela: token 1 = ligado; 0 ou ausente = desligado.</summary>
    public static OptionState Windowed(int? token) => token == 1 ? OptionState.On : OptionState.Off;

    /// <summary>Fortnite "Modo desempenho": DirectX 12 + nível es31 (as duas ações do botão).</summary>
    public static OptionState FortnitePerformance(FortniteState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.FileFound) return OptionState.Unknown;
        return string.Equals(s.Rhi, "dx12", StringComparison.OrdinalIgnoreCase) && s.IsPerformanceMode ? OptionState.On : OptionState.Off;
    }

    /// <summary>Fortnite "Menos atraso": o limite sugerido (quando há) e o Reflex ligado (nas placas NVIDIA).</summary>
    public static OptionState FortniteLatency(FortniteState s, string? cap, bool nvidia)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.FileFound || (cap is null && !nvidia)) return OptionState.Unknown;
        var capOk = cap is null || SameNumber(s.FrameRateLimit, cap);
        var reflexOk = !nvidia || s.Reflex is FortniteSettings.ReflexOn or FortniteSettings.ReflexOnBoost;
        return capOk && reflexOk ? OptionState.On : OptionState.Off;
    }

    /// <summary>Fortnite "Imagem nítida": desfoque de movimento desligado.</summary>
    public static OptionState FortniteClear(FortniteState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.FileFound) return OptionState.Unknown;
        return string.Equals(s.MotionBlur, "False", StringComparison.OrdinalIgnoreCase) ? OptionState.On : OptionState.Off;
    }

    private static bool SameNumber(string? a, string b) =>
        a is not null && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
        double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) < 0.01;
}

/// <summary>
/// Uma opção da tela Jogos: o que pede ao aplicador, como reconhecer no diário a execução que a ligou e como ler o estado real.
/// </summary>
/// <param name="Id">Identidade estável (executável + opção): o botão continua o mesmo entre aberturas da tela.</param>
public sealed record GameOptionPlan(string Id, IReadOnlyList<(ActionRequest Request, BrokerLevel Level)> Requests, IReadOnlyList<OptionPart> Parts,
    Func<IGameStateSource, OptionState> Read);

/// <summary>Estado lido de uma opção e como desfazê-la.</summary>
public sealed record OptionStatus(OptionState State, OptionUndo Undo)
{
    public static readonly OptionStatus Unread = new(OptionState.Unknown, OptionUndo.None);
}

/// <summary>As opções por jogo, com as mesmas ações e argumentos que a tela pede (uma fonte só para pedir e para ler).</summary>
public static class GameOptionPlans
{
    public const string WindowedAction = "win.windowed-game-optimizations.per-app@1";

    private static IReadOnlyList<OptionPart> Parts(string actionId, IReadOnlyList<string> exes) => [.. exes.Select(e => new OptionPart(actionId, e))];

    private static Dictionary<string, string> Exe(string exe) => new() { ["exe"] = exe };

    public static GameOptionPlan NvLowLatency(string exe)
    {
        var exes = GameOptionStates.NvTargets(exe);
        return new(exe + "|nv.lowlatency", [.. exes.Select(e => (new ActionRequest(NvidiaDrsActions.LowLatency, null, Exe(e)), BrokerLevel.Admin))],
            Parts(NvidiaDrsActions.LowLatency, exes), s => GameOptionStates.NvLowLatency(s.Nvidia, exes));
    }

    public static GameOptionPlan NvMaxPower(string exe)
    {
        var exes = GameOptionStates.NvTargets(exe);
        return new(exe + "|nv.maxpower", [.. exes.Select(e => (new ActionRequest(NvidiaDrsActions.MaxPower, null, Exe(e)), BrokerLevel.Admin))],
            Parts(NvidiaDrsActions.MaxPower, exes), s => GameOptionStates.NvMaxPower(s.Nvidia, exes));
    }

    /// <param name="fps">Um dos <see cref="NvidiaDrsActions.FpsCaps"/> (allowedValues do catálogo).</param>
    public static GameOptionPlan NvFpsCap(string exe, string fps)
    {
        var exes = GameOptionStates.NvTargets(exe);
        return new(exe + "|nv.fps." + fps, [.. exes.Select(e => (new ActionRequest(NvidiaDrsActions.FpsCap, fps, Exe(e)), BrokerLevel.Admin))],
            Parts(NvidiaDrsActions.FpsCap, exes), s => GameOptionStates.NvFpsCap(s.Nvidia, exes, fps));
    }

    public static GameOptionPlan Windowed(string exe, string exePath) =>
        new(exe + "|windowed", [(new ActionRequest(WindowedAction, null, new Dictionary<string, string> { ["gameExePath"] = exePath }), BrokerLevel.User)],
            [new OptionPart(WindowedAction, exePath)], s => GameOptionStates.Windowed(s.WindowedToken(exePath)));

    public static GameOptionPlan FortnitePerformance() =>
        new("fortnite|perf",
            [(new ActionRequest(FortniteSettings.RhiDx12Action, null), BrokerLevel.User), (new ActionRequest(FortniteSettings.PerformanceModeAction, null), BrokerLevel.User)],
            [new OptionPart(FortniteSettings.RhiDx12Action), new OptionPart(FortniteSettings.PerformanceModeAction)],
            s => GameOptionStates.FortnitePerformance(s.Fortnite()));

    /// <summary>Null quando não há o que pedir (sem limite sugerido e placa que não é NVIDIA).</summary>
    public static GameOptionPlan? FortniteLatency(string? cap, bool nvidia)
    {
        var requests = new List<(ActionRequest, BrokerLevel)>();
        var parts = new List<OptionPart>();
        if (cap is not null) { requests.Add((new ActionRequest(FortniteSettings.FpsLimitAction, cap), BrokerLevel.User)); parts.Add(new(FortniteSettings.FpsLimitAction)); }
        if (nvidia) { requests.Add((new ActionRequest(FortniteSettings.ReflexAction, FortniteSettings.ReflexOn), BrokerLevel.User)); parts.Add(new(FortniteSettings.ReflexAction)); }
        return requests.Count == 0 ? null
            : new("fortnite|latency|" + (cap ?? "-") + (nvidia ? "|reflex" : ""), requests, parts, s => GameOptionStates.FortniteLatency(s.Fortnite(), cap, nvidia));
    }

    public static GameOptionPlan FortniteClear() =>
        new("fortnite|clear", [(new ActionRequest(FortniteSettings.MotionBlurOffAction, null), BrokerLevel.User)],
            [new OptionPart(FortniteSettings.MotionBlurOffAction)], s => GameOptionStates.FortniteClear(s.Fortnite()));
}

/// <summary>Lê o estado de várias opções de uma vez (fora da thread da interface) e acha no diário o que desfaz cada uma.</summary>
public static class GameStateReader
{
    public static IReadOnlyDictionary<string, OptionStatus> Read(IEnumerable<GameOptionPlan> plans, IGameStateSource source, IReadOnlyList<JournalRun> journal)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(journal);
        var result = new Dictionary<string, OptionStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            OptionState state;
            try { state = plan.Read(source); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { state = OptionState.Unknown; }
            result[plan.Id] = new OptionStatus(state, state == OptionState.On ? OptionJournal.Find(journal, plan.Parts) : OptionUndo.None);
        }
        return result;
    }
}
