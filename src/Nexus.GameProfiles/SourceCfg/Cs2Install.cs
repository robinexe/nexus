using System.Text.RegularExpressions;
using Nexus.Core;

namespace Nexus.GameProfiles.SourceCfg;

/// <summary>
/// Onde fica a configuração do Counter-Strike 2 numa biblioteca Steam e de onde vem o dump do <c>cvarlist</c> que valida a
/// ação <c>cs2.cfg.fps_max</c>. Só leitura. A regra do caminho é a mesma de <c>userParameters.cs2CfgDir</c> do catálogo
/// (o teste confere), e o aplicador a revalida.
/// </summary>
public static partial class Cs2Install
{
    public const string FpsMaxActionKey = "cs2.cfg.fps_max@1";
    public const string InstallFolder = "Counter-Strike Global Offensive";

    /// <summary>Mesma regra do catálogo (cs2CfgDir): pasta cfg dentro de steamapps\common\Counter-Strike Global Offensive\game\csgo, sem "." nem "..".</summary>
    public const string CfgDirPattern = @"(?i)[A-Z]:\\(?:(?!\.\.?\\)[^\\/:*?""<>|\x00-\x1f]{1,120}\\){0,8}steamapps\\common\\Counter-Strike Global Offensive\\game\\csgo\\cfg";

    /// <summary>Valores permitidos de fps_max (mesma lista de allowedValues do catálogo). 0 = sem limite; o padrão do jogo é 400.</summary>
    public static IReadOnlyList<string> FpsMaxValues { get; } = ["0", "60", "120", "141", "144", "162", "165", "200", "237", "240", "300", "400"];

    /// <summary>Maior console.log lido (o dump do cvarlist tem poucas centenas de KB; o resto do log não interessa).</summary>
    public const long MaxConsoleLogBytes = 32L * 1024 * 1024;

    [GeneratedRegex(@"\A(?:" + CfgDirPattern + @")\z", RegexOptions.CultureInvariant)]
    private static partial Regex CfgDirRegex();

    [GeneratedRegex(@"\b\d+\s+total\s+convars/concommands\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CvarListFooter();

    public static bool IsValidCfgDir(string? dir) => dir is { Length: <= 260 } && CfgDirRegex().IsMatch(dir);

    /// <summary>Pasta de instalação (manifesto Steam, appid 730) → pasta cfg; null se não for o CS2 ou o caminho não passar na regra.</summary>
    public static string? CfgDirFromInstallDir(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return null;
        var root = installDir.TrimEnd('\\', '/');
        if (!root.EndsWith(@"\steamapps\common\" + InstallFolder, StringComparison.OrdinalIgnoreCase)) return null;
        var cfg = root + @"\game\csgo\cfg";
        return IsValidCfgDir(cfg) ? cfg : null;
    }

    /// <summary>console.log que o jogo grava com -condebug (em game\csgo, ao lado da pasta cfg).</summary>
    public static string ConsoleLogPath(string cfgDir) => GameDir(cfgDir) + SeparatorOf(cfgDir) + "console.log";

    /// <summary>Pasta acima de cfg (game\csgo), pelo texto do caminho (funciona igual com "\" e "/").</summary>
    private static string GameDir(string cfgDir)
    {
        ArgumentNullException.ThrowIfNull(cfgDir);
        var trimmed = cfgDir.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut > 0 ? trimmed[..cut] : trimmed;
    }

    private static char SeparatorOf(string path) => path.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';

    /// <summary>
    /// Texto parece um dump de cvarlist: tem o rodapé "N total convars/concommands" ou, sem ele, muitas linhas no formato
    /// "nome : valor : ...". Um console.log comum de partida não passa.
    /// </summary>
    public static CvarCatalog? FromConsoleLog(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        var catalog = CvarCatalog.FromCvarListDump(text, source);
        if (catalog.Count == 0) return null;
        return CvarListFooter().IsMatch(text) || catalog.Count >= 500 ? catalog : null;
    }

    /// <summary>Dump da instalação cujo cfg é o alvo da ação (alvo "root"). Null = sem dump válido (o handler bloqueia e explica).</summary>
    public static CvarCatalog? CvarsFor(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!action.Target.TryGetValue("root", out var root) || !IsValidCfgDir(root)) return null;
        return ReadDump(root);
    }

    /// <summary>fps_max atribuído hoje no autoexec.cfg (null = não atribuído ou arquivo ausente). Só leitura, sem seguir links.</summary>
    public static string? ReadFpsMax(string cfgDir)
    {
        ArgumentNullException.ThrowIfNull(cfgDir);
        try
        {
            var path = SafeFiles.EnsureUnderRoot(cfgDir, Path.Combine(cfgDir, "autoexec.cfg"));
            return SourceCfgDocument.Parse(SafeFiles.Read(path).Text).GetEffective("fps_max");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Lê o console.log ao lado da pasta cfg sem seguir links e com teto de tamanho.</summary>
    public static CvarCatalog? ReadDump(string cfgDir)
    {
        ArgumentNullException.ThrowIfNull(cfgDir);
        try
        {
            var path = SafeFiles.EnsureUnderRoot(GameDir(cfgDir), ConsoleLogPath(cfgDir));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxConsoleLogBytes) return null;
            var text = File.ReadAllText(path);
            return FromConsoleLog(text, $"console.log de {info.LastWriteTime:dd/MM/yyyy HH:mm}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
