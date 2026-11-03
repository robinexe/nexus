using System.Globalization;

namespace Nexus.GameProfiles.Fortnite;

/// <summary>Uma chave do GameUserSettings.ini que o NEXUS pode mudar, com a regra do valor que ele pode ESCREVER.</summary>
public sealed record FortniteKey(string Section, string Key, Func<string, bool> Accepts);

/// <summary>
/// Regras fixas do Fortnite (PC): onde fica o arquivo, quais processos impedem a edição e a lista FECHADA de chaves e valores.
/// O caminho nunca vem do catálogo nem do usuário: é sempre
/// <c>&lt;AppData\Local do perfil&gt;\FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini</c>.
/// Nada aqui toca Engine.ini, Scalability.ini, arquivos do jogo, .pak ou o Easy Anti-Cheat (docs/modules/fortnite.md).
/// </summary>
public static class FortniteSettings
{
    public const string FileName = "GameUserSettings.ini";

    /// <summary>Pasta do arquivo relativa a AppData\Local.</summary>
    public static readonly string[] RelativeDirectory = ["FortniteGame", "Saved", "Config", "WindowsClient"];

    public const string MainSection = "/Script/FortniteGame.FortGameUserSettings";
    public const string ScalabilitySection = "ScalabilityGroups";
    public const string RhiSection = "D3DRHIPreference";

    /// <summary>Executável do jogo (o mesmo nome que a biblioteca de jogos e o Medir usam).</summary>
    public const string GameExe = "FortniteClient-Win64-Shipping.exe";

    /// <summary>
    /// Processos (sem .exe) que, abertos, regravam o GameUserSettings.ini ao sair: o jogo, suas variantes com o anticheat e o
    /// lançador do próprio jogo. O Epic Games Launcher não regrava este arquivo e não bloqueia.
    /// </summary>
    public static readonly IReadOnlyList<string> BlockingProcesses =
    [
        "FortniteClient-Win64-Shipping", "FortniteClient-Win64-Shipping_EAC", "FortniteClient-Win64-Shipping_EAC_EOS",
        "FortniteClient-Win64-Shipping_BE", "FortniteLauncher",
    ];

    // ---- ids e valores do catálogo (catalog/fortnite.v1.json); o teste confere que batem com o catálogo
    public const string RhiDx12Action = "fortnite.render.rhi.dx12@1";
    public const string PerformanceModeAction = "fortnite.render.performance-mode@1";
    public const string Dx12FullAction = "fortnite.render.dx12-full@1";
    public const string FpsLimitAction = "fortnite.fps.limit@1";
    public const string ReflexAction = "fortnite.latency.reflex@1";
    public const string VSyncOffAction = "fortnite.display.vsync.off@1";
    public const string MotionBlurOffAction = "fortnite.visual.motion-blur.off@1";
    public const string ShadowsOffAction = "fortnite.visual.shadows.off@1";
    public const string GrassOffAction = "fortnite.visual.grass.off@1";

    /// <summary>LatencyTweak2: 0 = Reflex desligado, 1 = ligado, 2 = ligado + boost.</summary>
    public const string ReflexOn = "1";
    public const string ReflexOnBoost = "2";

    /// <summary>Limites de FPS aceitos (como o jogo grava: float com 6 casas). 0 = sem limite.</summary>
    public static IReadOnlyList<string> FpsLimits { get; } =
    [
        "0.000000", "30.000000", "57.000000", "60.000000", "72.000000", "75.000000", "87.000000", "90.000000", "97.000000",
        "117.000000", "120.000000", "141.000000", "144.000000", "162.000000", "165.000000", "177.000000", "197.000000",
        "237.000000", "240.000000", "357.000000", "360.000000",
    ];

    private static readonly string[] Bools = ["True", "False"];

    private static bool OneOf(string v, params string[] allowed) => allowed.Contains(v, StringComparer.Ordinal);

    /// <summary>A lista fechada: só estas chaves, só estes valores na escrita (o desfazer pode devolver o valor original que estava lá).</summary>
    public static IReadOnlyList<FortniteKey> Allowed { get; } =
    [
        new(RhiSection, "PreferredRHI", v => OneOf(v, "dx12")),
        new(RhiSection, "PreferredFeatureLevel", v => OneOf(v, "es31", "sm6")),
        new(MainSection, "FrameRateLimit", v => FpsLimits.Contains(v, StringComparer.Ordinal)),
        new(MainSection, "LatencyTweak2", v => OneOf(v, "0", ReflexOn, ReflexOnBoost)),
        new(MainSection, "bUseVSync", v => OneOf(v, Bools)),
        new(MainSection, "bMotionBlur", v => OneOf(v, Bools)),
        new(MainSection, "bShowGrass", v => OneOf(v, Bools)),
        new(ScalabilitySection, "sg.ShadowQuality", v => OneOf(v, "0", "1", "2", "3")),
    ];

    public static FortniteKey? Find(string? section, string? key) =>
        section is null || key is null ? null
            : Allowed.FirstOrDefault(k => string.Equals(k.Section, section, StringComparison.Ordinal) && string.Equals(k.Key, key, StringComparison.Ordinal));

    /// <summary>Caminho do arquivo a partir de AppData\Local do perfil.</summary>
    public static string FilePath(string localAppData)
    {
        ArgumentNullException.ThrowIfNull(localAppData);
        return Path.Combine([localAppData, .. RelativeDirectory, FileName]);
    }

    /// <summary>Limite sugerido: 3 abaixo da taxa do monitor (fica dentro da faixa do G-SYNC/FreeSync), o maior da lista que não passa disso.</summary>
    public static string? SuggestedFpsLimit(int refreshHz)
    {
        if (refreshHz < 50) return null;
        var target = refreshHz - 3;
        return FpsLimits.Select(v => (Text: v, Value: double.Parse(v, CultureInfo.InvariantCulture)))
            .Where(x => x.Value > 0 && x.Value <= target).OrderByDescending(x => x.Value).Select(x => x.Text).FirstOrDefault();
    }

    /// <summary>"141.000000" → "141" (para mostrar).</summary>
    public static string FpsLabel(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("0", CultureInfo.InvariantCulture) : value;
}

/// <summary>Leitura simples (só leitura) do estado atual para as telas: modo de renderização, limite, Reflex.</summary>
public sealed record FortniteState(bool FileFound, string? Rhi, string? FeatureLevel, string? FrameRateLimit, string? Reflex, string? VSync,
    string? MotionBlur, string? Shadows, string? Grass)
{
    public bool IsPerformanceMode => string.Equals(FeatureLevel, "es31", StringComparison.OrdinalIgnoreCase);

    public static FortniteState NotFound { get; } = new(false, null, null, null, null, null, null, null, null);

    public static FortniteState FromText(string text)
    {
        var doc = IniDocument.Parse(text);
        string? V(string s, string k) => doc.Get(s, k) is (IniKeyState.Present, var v) ? v : null;
        return new FortniteState(true, V(FortniteSettings.RhiSection, "PreferredRHI"), V(FortniteSettings.RhiSection, "PreferredFeatureLevel"),
            V(FortniteSettings.MainSection, "FrameRateLimit"), V(FortniteSettings.MainSection, "LatencyTweak2"), V(FortniteSettings.MainSection, "bUseVSync"),
            V(FortniteSettings.MainSection, "bMotionBlur"), V(FortniteSettings.ScalabilitySection, "sg.ShadowQuality"), V(FortniteSettings.MainSection, "bShowGrass"));
    }

    /// <summary>Lê o arquivo do perfil (sem seguir links). Arquivo ausente/ilegível = <see cref="NotFound"/>.</summary>
    public static FortniteState Read(string localAppData)
    {
        try
        {
            var path = SafeFiles.EnsureUnderRoot(localAppData, FortniteSettings.FilePath(localAppData));
            var read = IniFiles.Read(path);
            return read.Exists ? FromText(read.Text) : NotFound;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            return NotFound;
        }
    }
}
