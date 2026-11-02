using System.Windows.Markup;

namespace Nexus.Desktop.Localization;

/// <summary>
/// <c>Text="{l:T Chave}"</c> com <c>xmlns:l="clr-namespace:Nexus.Desktop.Localization"</c>: texto fixo da interface no idioma
/// escolhido (ver <see cref="Loc"/>). Resolvido uma vez ao carregar o XAML; trocar de idioma pede reinício.
/// Também serve dentro de um Binding: <c>StringFormat={l:T Chave}</c>.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
