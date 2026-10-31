using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Um passo do "como funciona": ícone, verbo curto e uma frase. Declarado direto no XAML dentro de <see cref="HowItWorks"/>.
/// <see cref="Number"/> e <see cref="IsLast"/> são preenchidos pelo HowItWorks (a ordem no XAML é a ordem dos passos).
/// </summary>
public sealed class StepItem : INotifyPropertyChanged
{
    private int _number;
    private bool _isLast;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Ícone de Themes/Icons.xaml (ex.: {StaticResource Icon.Search}).</summary>
    public Geometry? Icon { get; set; }

    /// <summary>Verbo curto: "Varre", "Guarda", "Desfaz".</summary>
    public string Title { get; set; } = "";

    /// <summary>Uma frase (até ~70 caracteres).</summary>
    public string Text { get; set; } = "";

    /// <summary>Tom do passo: Warn (âmbar) para o passo principal, Bad para o irreversível, Info (padrão) para o resto.</summary>
    public Tone Tone { get; set; } = Tone.Info;

    public int Number { get => _number; internal set => Set(ref _number, value); }
    public bool IsLast { get => _isLast; internal set => Set(ref _isLast, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// "Como funciona" (demonstrativo didático): 3 ou 4 passos lado a lado, numerados e ligados por uma linha. Cada passo usa o
/// modelo "StepCard" de Themes/Components.xaml.
/// <code>
/// &lt;c:HowItWorks&gt;
///   &lt;c:StepItem Icon="{StaticResource Icon.Search}" Title="Varre" Text="Mede o que dá para liberar, sem mexer em nada."/&gt;
///   &lt;c:StepItem Icon="{StaticResource Icon.Quarantine}" Title="Guarda" Text="O que sai vai para a quarentena." Tone="Warn"/&gt;
/// &lt;/c:HowItWorks&gt;
/// </code>
/// </summary>
public class HowItWorks : ItemsControl
{
    public HowItWorks()
    {
        Focusable = false;
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        var steps = Items.OfType<StepItem>().ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            steps[i].Number = i + 1;
            steps[i].IsLast = i == steps.Count - 1;
        }
    }
}
