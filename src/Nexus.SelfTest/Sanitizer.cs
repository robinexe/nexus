using System.Text.RegularExpressions;

namespace Nexus.SelfTest;

/// <summary>
/// Tira dados pessoais dos textos do relatório (mensagens de erro, pilhas, avisos): caminho do perfil, nome do usuário,
/// nome do computador, e-mails e qualquer <c>X:\Users\&lt;nome&gt;</c> ou <c>/home/&lt;nome&gt;</c>. As verificações já só anotam
/// contagens; isto cobre o que vem de exceções e mensagens das ferramentas do Windows.
/// </summary>
public static partial class Sanitizer
{
    private sealed record Identity(string? Profile, string? User, string? Machine);

    private static readonly Identity Current = new(
        SafeGet(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        SafeGet(() => Environment.UserName),
        SafeGet(() => Environment.MachineName));

    private static string? SafeGet(Func<string> read)
    {
        try { var s = read(); return string.IsNullOrWhiteSpace(s) ? null : s; }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException) { return null; }
    }

    public static string Clean(string? text) => Clean(text, Current.Profile, Current.User, Current.Machine);

    /// <summary>Versão testável (identidade explícita).</summary>
    public static string Clean(string? text, string? profile, string? user, string? machine)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        if (profile is { Length: >= 4 })
        {
            s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            s = s.Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        s = WindowsUserPath().Replace(s, m => m.Groups[1].Value + "<usuário>");
        s = UnixHomePath().Replace(s, "/home/<usuário>");
        s = Email().Replace(s, "<e-mail>");
        // Nomes curtos demais ("pc", "ana") trocariam pedaços de palavras: só nomes com 3+ caracteres e como palavra inteira.
        if (machine is { Length: >= 3 }) s = Regex.Replace(s, $@"(?<![\p{{L}}\p{{N}}_-]){Regex.Escape(machine)}(?![\p{{L}}\p{{N}}_-])", "<pc>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (user is { Length: >= 3 }) s = Regex.Replace(s, $@"(?<![\p{{L}}\p{{N}}_-]){Regex.Escape(user)}(?![\p{{L}}\p{{N}}_-])", "<usuário>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return s;
    }

    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
    }

    [GeneratedRegex(@"([A-Za-z]:[\\/]+(?:Users|Usuários|Documents and Settings)[\\/]+)(?!<usuário>)[^\\/\s""'<>:|*?]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WindowsUserPath();

    [GeneratedRegex(@"/home/(?!<usuário>)[^/\s""']+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UnixHomePath();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Email();
}
