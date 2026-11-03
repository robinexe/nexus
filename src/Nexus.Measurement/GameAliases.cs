namespace Nexus.Measurement;

/// <summary>
/// Lançador/inicializador x processo que DESENHA os quadros. O PresentMon só vê quadros do processo que apresenta (o que
/// chama Present); muitos jogos abrem por um executável que só prepara o anticheat ou o lançador e depois some ou fica parado:
/// <list type="bullet">
/// <item>Fortnite: FortniteLauncher.exe → FortniteClient-Win64-Shipping_EAC_EOS.exe / _EAC / _BE (inicializa o anticheat) →
/// FortniteClient-Win64-Shipping.exe (desenha);</item>
/// <item>Unreal em geral: &lt;Nome&gt;.exe na raiz da pasta → &lt;Nome&gt;\Binaries\Win64\&lt;Nome&gt;-Win64-Shipping.exe;</item>
/// <item>Riot (VALORANT.exe → VALORANT-Win64-Shipping.exe; LeagueClient → League of Legends.exe), Rockstar (PlayGTAV.exe →
/// GTA5.exe), FromSoftware (start_protected_game.exe → eldenring.exe), BSG, Bungie, NetEase, BattlEye (_BE.exe).</item>
/// </list>
/// Nomes conferidos em listas públicas de processos (Gerenciador de Tarefas, aba Detalhes; ver docs/modules/measure.md). A regra
/// genérica (sufixos de anticheat e "-Win64-Shipping") cobre jogos fora da lista; o resto fica com a escolha pelo que está
/// desenhando (<see cref="LiveTargetPicker"/>).
/// </summary>
public static class GameAliases
{
    /// <summary>Executável de lançador/inicializador → processos que desenham (em ordem de preferência).</summary>
    private static readonly Dictionary<string, string[]> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FortniteLauncher.exe"] = ["FortniteClient-Win64-Shipping.exe"],
        ["FortniteClient-Win64-Shipping_EAC_EOS.exe"] = ["FortniteClient-Win64-Shipping.exe"],
        ["FortniteClient-Win64-Shipping_EAC.exe"] = ["FortniteClient-Win64-Shipping.exe"],
        ["FortniteClient-Win64-Shipping_BE.exe"] = ["FortniteClient-Win64-Shipping.exe"],
        ["VALORANT.exe"] = ["VALORANT-Win64-Shipping.exe"],
        ["LeagueClient.exe"] = ["League of Legends.exe"],
        ["LeagueClientUx.exe"] = ["League of Legends.exe"],
        ["RiotClientServices.exe"] = ["VALORANT-Win64-Shipping.exe", "League of Legends.exe"],
        ["PlayGTAV.exe"] = ["GTA5.exe", "GTA5_Enhanced.exe"],
        ["GTAVLauncher.exe"] = ["GTA5.exe"],
        ["start_protected_game.exe"] = ["eldenring.exe", "nightreign.exe", "armoredcore6.exe"],
        ["BsgLauncher.exe"] = ["EscapeFromTarkov.exe"],
        ["destiny2launcher.exe"] = ["destiny2.exe"],
        ["MarvelRivals_Launcher.exe"] = ["Marvel-Win64-Shipping.exe"],
        ["MarvelGame.exe"] = ["Marvel-Win64-Shipping.exe"],
        ["TslGame_BE.exe"] = ["TslGame.exe"],
        ["ExecPubg.exe"] = ["TslGame.exe"],
        ["RainbowSix_BE.exe"] = ["RainbowSix.exe", "RainbowSix_Vulkan.exe"],
        ["r5apex_launcher.exe"] = ["r5apex.exe", "r5apex_dx12.exe"],
        ["RocketLeague_BE.exe"] = ["RocketLeague.exe"],
        ["DeadByDaylight.exe"] = ["DeadByDaylight-Win64-Shipping.exe"],
        ["Palworld.exe"] = ["Palworld-Win64-Shipping.exe"],
        ["MinecraftLauncher.exe"] = ["javaw.exe", "Minecraft.Windows.exe"],
        ["Overwatch Launcher.exe"] = ["Overwatch.exe"],
        ["Diablo IV Launcher.exe"] = ["Diablo IV.exe"],
        ["World of Warcraft Launcher.exe"] = ["Wow.exe"],
        ["RustClient_EAC.exe"] = ["RustClient.exe"],
        ["Rust.exe"] = ["RustClient.exe"],
    };

    /// <summary>Sufixos de inicializadores de anticheat (EasyAntiCheat, Epic Online Services, BattlEye) no nome do executável.</summary>
    private static readonly string[] BootstrapSuffixes = ["_EAC_EOS", "_EAC", "_EOS", "_BE"];

    /// <summary>O executável é um lançador/inicializador conhecido (ou tem sufixo de anticheat): não é ele que desenha.</summary>
    public static bool IsBootstrapper(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        if (Known.ContainsKey(exe)) return true;
        var stem = Path.GetFileNameWithoutExtension(exe);
        return BootstrapSuffixes.Any(s => stem.EndsWith(s, StringComparison.OrdinalIgnoreCase) && stem.Length > s.Length);
    }

    /// <summary>
    /// Processos que provavelmente desenham por este executável, em ordem: os da lista conhecida, o nome sem sufixo de anticheat
    /// (FortniteClient-Win64-Shipping_EAC_EOS.exe → FortniteClient-Win64-Shipping.exe) e o padrão Unreal
    /// (Jogo.exe → Jogo-Win64-Shipping.exe). Não inclui o próprio executável. Só nomes válidos (<see cref="PresentMonCli.IsValidExeName"/>).
    /// </summary>
    public static IReadOnlyList<string> RenderCandidates(string? exe)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(exe)) return list;
        if (Known.TryGetValue(exe, out var known)) list.AddRange(known);
        var stem = Path.GetFileNameWithoutExtension(exe);
        foreach (var s in BootstrapSuffixes)
            if (stem.EndsWith(s, StringComparison.OrdinalIgnoreCase) && stem.Length > s.Length) { list.Add(stem[..^s.Length] + ".exe"); break; }
        if (!stem.Contains("-Shipping", StringComparison.OrdinalIgnoreCase) && !IsBootstrapperSuffixOnly(stem))
            list.Add(stem + "-Win64-Shipping.exe");
        return list.Where(n => !n.Equals(exe, StringComparison.OrdinalIgnoreCase) && PresentMonCli.IsValidExeName(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsBootstrapperSuffixOnly(string stem) =>
        BootstrapSuffixes.Any(s => stem.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Entre os processos abertos, o que desenha por este jogo: um candidato de <see cref="RenderCandidates"/> que esteja rodando
    /// (o primeiro da ordem), ou null. Serve para medir o processo certo quando o detector achou o inicializador.
    /// </summary>
    public static RunningProcess? RunningRenderProcess(string exe, IEnumerable<RunningProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var running = processes.ToList();
        foreach (var name in RenderCandidates(exe))
        {
            var p = running.FirstOrDefault(r => GameLibrary.WinFileName(r.Path ?? r.Name).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                                 r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                                 (r.Name + ".exe").Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p is not null) return p;
        }
        return null;
    }

    /// <summary>Nomes que a captura deve aceitar para este jogo: o próprio executável + os candidatos que desenham.</summary>
    public static IReadOnlyList<string> CaptureNames(string exe) =>
        new[] { exe }.Concat(RenderCandidates(exe)).Where(PresentMonCli.IsValidExeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
