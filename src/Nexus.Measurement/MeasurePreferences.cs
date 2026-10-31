using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Measurement;

/// <summary>Canto da tela do mostrador de FPS.</summary>
public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Preferências da medição, num arquivo do usuário (%LocalAppData%\NEXUS\medir.json). Não é configuração do sistema:
/// nada aqui muda o Windows. Valores inválidos (lista com caminho, atalho desconhecido) são descartados ao carregar.
/// </summary>
public sealed record MeasurePreferences
{
    public bool AutoMeasure { get; init; }
    public bool OverlayOnGameStart { get; init; }
    /// <summary>Também considerar jogo qualquer programa em tela cheia (fora das bibliotecas e listas).</summary>
    public bool DetectFullscreen { get; init; }
    public IReadOnlyList<string> UserGames { get; init; } = [];
    public IReadOnlyList<string> ExcludedGames { get; init; } = [];
    public string OverlayHotkey { get; init; } = "Ctrl+Shift+F";
    public OverlayCorner OverlayCorner { get; init; } = OverlayCorner.TopLeft;
    public bool OverlayShowSystem { get; init; } = true;
    public string? PresentMonPath { get; init; }
    public bool RequestElevation { get; init; } = true;
    /// <summary>Sessões automáticas mais curtas que isto não são guardadas (menu, lobby, fechou logo).</summary>
    public int MinSessionSeconds { get; init; } = 60;
    /// <summary>0 = guardar para sempre; senão, a limpeza oferece apagar sessões mais velhas que isto (dias).</summary>
    public int KeepDays { get; init; }
    /// <summary>Quando o usuário viu pela última vez o convite "jogue para medirmos" (para não repetir a cada abertura).</summary>
    public DateTimeOffset? LastInviteDismissedAt { get; init; }

    public const int MaxListItems = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "medir.json");

    public static MeasurePreferences Load(string path)
    {
        try { return (JsonSerializer.Deserialize<MeasurePreferences>(File.ReadAllText(path), Json) ?? new()).Normalize(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return new(); }
    }

    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Normalize(), Json));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Remove itens inválidos/duplicados e limita valores.</summary>
    public MeasurePreferences Normalize() => this with
    {
        UserGames = Clean(UserGames),
        ExcludedGames = Clean(ExcludedGames),
        OverlayHotkey = Hotkey.TryParse(OverlayHotkey, out _) ? Hotkey.Parse(OverlayHotkey).ToString() : "Ctrl+Shift+F",
        OverlayCorner = Enum.IsDefined(OverlayCorner) ? OverlayCorner : OverlayCorner.TopLeft,
        MinSessionSeconds = Math.Clamp(MinSessionSeconds, 20, 1800),
        KeepDays = Math.Clamp(KeepDays, 0, 3650),
        PresentMonPath = PresentMonPath is { Length: > 0 and <= 260 } p && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : null,
    };

    private static List<string> Clean(IReadOnlyList<string>? items) =>
        (items ?? []).Select(s => (s ?? "").Trim()).Where(PresentMonCli.IsValidExeName).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxListItems).ToList();

    public MeasurePreferences WithUserGame(string exe) =>
        PresentMonCli.IsValidExeName(exe?.Trim())
            ? this with { UserGames = UserGames.Append(exe!.Trim()).ToList(), ExcludedGames = ExcludedGames.Where(e => !e.Equals(exe.Trim(), StringComparison.OrdinalIgnoreCase)).ToList() }
            : this;

    public MeasurePreferences WithExcluded(string exe) =>
        PresentMonCli.IsValidExeName(exe?.Trim())
            ? this with { ExcludedGames = ExcludedGames.Append(exe!.Trim()).ToList(), UserGames = UserGames.Where(e => !e.Equals(exe.Trim(), StringComparison.OrdinalIgnoreCase)).ToList() }
            : this;
}

/// <summary>Modificadores de RegisterHotKey (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8).</summary>
[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 }

/// <summary>
/// Atalho global ("Ctrl+Shift+F"), convertido para os valores de RegisterHotKey: modificadores + código de tecla virtual.
/// Exige pelo menos um modificador entre Ctrl/Alt/Win (só Shift + letra atrapalharia a digitação no jogo). Teclas: A–Z, 0–9, F1–F24.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey, string KeyName)
{
    public static Hotkey Parse(string text) => TryParse(text, out var h) ? h : throw new FormatException($"Atalho inválido: {text}");

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= HotkeyModifiers.Control; break;
                case "ALT": mods |= HotkeyModifiers.Alt; break;
                case "SHIFT": mods |= HotkeyModifiers.Shift; break;
                case "WIN" or "WINDOWS": mods |= HotkeyModifiers.Win; break;
                default:
                    if (key is not null || raw.Length == 0) return false;
                    key = raw.ToUpperInvariant();
                    break;
            }
        }
        if (key is null || (mods & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0) return false;
        int vk;
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z') vk = key[0];
        else if (key.Length == 1 && key[0] is >= '0' and <= '9') vk = key[0];
        else if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
            vk = 0x70 + f - 1;
        else return false;
        hotkey = new Hotkey(mods, vk, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName);
        return string.Join("+", parts);
    }
}
