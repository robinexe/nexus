using System.Windows;
using System.Windows.Controls;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// "Por que isso?": a página mostra pouco texto; a explicação longa (mecanismo, risco, fonte) fica recolhida aqui e abre com
/// um clique ou Enter/Espaço. Ao abrir, o texto entra com um fade curto (nada com animações desligadas).
/// <code>&lt;c:ExplainToggle&gt;&lt;TextBlock Text="{Binding QuarantineNote}" Style="{StaticResource Text.Small}"/&gt;&lt;/c:ExplainToggle&gt;</code>
/// Conteúdo de texto puro também serve: <c>&lt;c:ExplainToggle Content="{Binding DiskCleanupNote}"/&gt;</c> (vira Text.Small).
/// </summary>
[TemplatePart(Name = PartBody, Type = typeof(FrameworkElement))]
public class ExplainToggle : ContentControl
{
    private const string PartBody = "PART_Body";
    private FrameworkElement? _body;

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(string), typeof(ExplainToggle),
        new PropertyMetadata(Loc.T("Shell.Isso")));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(ExplainToggle),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnExpandedChanged));

    public ExplainToggle()
    {
        Focusable = false;
    }

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _body = GetTemplateChild(PartBody) as FrameworkElement;
    }

    private static void OnExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && d is ExplainToggle { _body: { } body }) ShellMotion.RevealIn(body);
    }
}
