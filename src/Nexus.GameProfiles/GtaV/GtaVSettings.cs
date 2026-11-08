using Nexus.GameProfiles.Configs;

namespace Nexus.GameProfiles.GtaV;

/// <summary>
/// Regras fixas do Grand Theft Auto V no PC (pesquisa de 08/11/2026, docs/modules/gtav.md). O jogo grava o menu de vídeo em
/// <c>Documentos\Rockstar Games\GTA V\settings.xml</c> (Legacy) e <c>Documentos\Rockstar Games\GTAV Enhanced\settings.xml</c>
/// (Enhanced, de 2025; falta confirmar em máquina real), um elemento por linha: <c>&lt;VSync value="0" /&gt;</c>. Só o valor
/// de linhas que o jogo já gravou, com o jogo fechado. Não há limitador de FPS no arquivo (o jogo não tem essa opção). Nada de
/// arquivos do jogo, mods ou pastas do BattlEye.
/// </summary>
public static class GtaVSettings
{
    public const string LegacyId = "gtav";
    public const string EnhancedId = "gtav-enhanced";

    /// <summary>Os que desenham primeiro (Legacy e Enhanced), depois os lançadores da Rockstar.</summary>
    public static readonly IReadOnlyList<string> Executables = ["GTA5.exe", "GTA5_Enhanced.exe", "PlayGTAV.exe", "GTAVLauncher.exe"];

    public const string VSyncKey = "VSync";
    public const string PauseOnFocusLossKey = "PauseOnFocusLoss";

    public const string LegacyVsyncOffAction = "gtav.cfg.vsync.off@1";
    public const string LegacyKeepRunningAction = "gtav.cfg.pause-on-focus-loss.off@1";
    public const string EnhancedVsyncOffAction = "gtav.enhanced.vsync.off@1";
    public const string EnhancedKeepRunningAction = "gtav.enhanced.pause-on-focus-loss.off@1";

    private static IReadOnlyList<GameConfigKey> Keys { get; } =
    [
        new(VSyncKey, ["0"]),             // menu Gráficos › VSync: Desligado (0 é o valor de um settings.xml real com VSync desligado)
        new(PauseOnFocusLossKey, ["0"]),  // menu Gráficos › Pausar jogo ao perder o foco: Desligado
    ];

    /// <summary>Processos que, abertos, regravam o settings.xml ao sair: o jogo e o inicializador dele (o Rockstar Games Launcher em si não bloqueia).</summary>
    public static GameConfig Legacy { get; } = new(LegacyId, "GTA V", ConfigRoot.Documents, @"Rockstar Games\GTA V\settings.xml", ConfigSyntax.XmlAttribute,
        ["GTA5", "PlayGTAV", "GTAVLauncher"], Keys);

    public static GameConfig Enhanced { get; } = new(EnhancedId, "GTA V Enhanced", ConfigRoot.Documents, @"Rockstar Games\GTAV Enhanced\settings.xml", ConfigSyntax.XmlAttribute,
        ["GTA5_Enhanced", "PlayGTAV"], Keys);
}

public sealed class GtaVConfigProvider : IGameConfigProvider
{
    public IReadOnlyList<GameConfig> Games { get; } = [GtaVSettings.Legacy, GtaVSettings.Enhanced];
}
