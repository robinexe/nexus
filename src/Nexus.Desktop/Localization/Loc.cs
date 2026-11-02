using System.Globalization;
using System.Resources;

namespace Nexus.Desktop.Localization;

/// <summary>
/// Textos da interface em pt-BR (padrão, <c>Strings.resx</c>), inglês (<c>Strings.en.resx</c>) e espanhol
/// (<c>Strings.es.resx</c>). XAML usa <c>{l:T Chave}</c> (<see cref="TExtension"/>); código usa <see cref="T"/> e
/// <see cref="F"/>. O idioma é escolhido uma vez, antes da primeira janela (<see cref="Use"/>, chamado pelo construtor de
/// App), e vale até reiniciar: trocar em Ajustes pede reinício. Chave ausente num idioma cai no pt-BR (o ResourceManager
/// faz isso sozinho); chave ausente em tudo devolve a própria chave, para aparecer na tela e não derrubar nada.
/// Lógica pura (sem WPF): compilada também no projeto de testes (LocalizationTests).
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Resources = new("Nexus.Desktop.Localization.Strings", typeof(Loc).Assembly);
    private static CultureInfo _culture = CultureInfo.GetCultureInfo(LanguageOption.Default);

    /// <summary>Cultura dos textos (pt-BR, en ou es).</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>Código do idioma em uso: "pt-BR", "en" ou "es".</summary>
    public static string Language => _culture.Name;

    /// <summary>Escolhe o idioma (código já resolvido por <see cref="LanguageOption.Resolve"/>). Desconhecido = pt-BR.</summary>
    public static void Use(string language) =>
        _culture = CultureInfo.GetCultureInfo(LanguageOption.Supported.Contains(language, StringComparer.OrdinalIgnoreCase) ? language : LanguageOption.Default);

    /// <summary>Texto da chave no idioma atual.</summary>
    public static string T(string key) => Get(key, _culture);

    /// <summary>Texto da chave com <see cref="string.Format(IFormatProvider, string, object[])"/> ({0}, {1:0.0}...),
    /// números e datas no formato do idioma atual.</summary>
    public static string F(string key, params object?[] args)
    {
        var format = Get(key, _culture);
        try { return string.Format(_culture, format, args); }
        catch (FormatException) { return format; } // tradução com marcador errado: mostra o texto cru, não derruba a janela
    }

    /// <summary>Texto da chave numa cultura escolhida (testes e relatórios).</summary>
    public static string Get(string key, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(key);
        try { return Resources.GetString(key, culture) ?? key; }
        catch (MissingManifestResourceException) { return key; }
    }
}
