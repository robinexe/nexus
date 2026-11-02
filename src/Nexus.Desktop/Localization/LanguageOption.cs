using System.Globalization;

namespace Nexus.Desktop.Localization;

/// <summary>
/// Idiomas da interface e como escolher um. Preferência em Ajustes (<c>settings.json</c>, campo <c>language</c>): null ou
/// "system" = igual ao Windows; "pt-BR", "en", "es" = fixo. <c>--lang xx</c> na linha de comando vence a preferência (usado
/// pelo autoteste para fotografar outro idioma). Windows em idioma sem tradução = pt-BR.
/// Para acrescentar um idioma: crie Strings.&lt;código&gt;.resx com TODAS as chaves (LocalizationTests confere), inclua o
/// código em <see cref="Supported"/> e <see cref="Choices"/>, e em SatelliteResourceLanguages (tools/packaging/publish.py).
/// </summary>
public static class LanguageOption
{
    public const string Default = "pt-BR";
    public const string System = "system";

    /// <summary>Códigos com tradução completa (Strings.resx = pt-BR).</summary>
    public static IReadOnlyList<string> Supported { get; } = ["pt-BR", "en", "es"];

    /// <summary>Opções da lista em Ajustes: código e nome no próprio idioma (não traduzido de propósito).</summary>
    public static IReadOnlyList<(string Code, string NativeName)> Choices { get; } =
        [("pt-BR", "Português (Brasil)"), ("en", "English"), ("es", "Español")];

    /// <summary>Idioma suportado mais próximo de uma cultura do Windows (pt-PT → pt-BR, en-GB → en, es-MX → es; resto → pt-BR).</summary>
    public static string FromCulture(CultureInfo? culture)
    {
        for (var c = culture; c is not null && !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            switch (c.TwoLetterISOLanguageName)
            {
                case "pt": return "pt-BR";
                case "en": return "en";
                case "es": return "es";
            }
        }
        return Default;
    }

    /// <summary>Normaliza um código digitado/gravado ("EN", "es-AR", "pt") para um suportado; null se não reconhecer.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Trim();
        if (string.Equals(trimmed, System, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var s in Supported)
            if (string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase)) return s;
        var dash = trimmed.IndexOfAny(['-', '_']);
        var lang = (dash > 0 ? trimmed[..dash] : trimmed).ToLowerInvariant();
        return lang switch { "pt" => "pt-BR", "en" => "en", "es" => "es", _ => null };
    }

    /// <summary>Idioma efetivo: <c>--lang</c> &gt; preferência gravada &gt; idioma do Windows &gt; pt-BR.</summary>
    public static string Resolve(string? commandLine, string? setting, CultureInfo? windowsUi) =>
        Normalize(commandLine) ?? Normalize(setting) ?? FromCulture(windowsUi);

    /// <summary>Valor de <c>--lang xx</c> (ou <c>--lang=xx</c>) nos argumentos, ou null.</summary>
    public static string? FromArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase)) return a["--lang=".Length..];
            if (string.Equals(a, "--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) return args[i + 1];
        }
        return null;
    }
}
