using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nexus.Tests;

/// <summary>
/// Propriedades do WPF que gravam de volta por padrão (ProgressBar.Value, CheckBox.IsChecked, TextBox.Text...) não podem
/// ser ligadas a uma propriedade só de leitura do ViewModel sem <c>Mode=OneWay</c>: o WPF lança "TwoWay ou OneWayToSource
/// não pode funcionar na propriedade somente leitura" a cada atualização (visto no PC do responsável em 31/10, página Processador).
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class XamlBindingTests
{
    private static readonly HashSet<string> TwoWayByDefault = new(StringComparer.Ordinal)
    {
        "ProgressBar.Value", "Slider.Value", "ScrollBar.Value", "RangeBase.Value",
        "CheckBox.IsChecked", "RadioButton.IsChecked", "ToggleButton.IsChecked",
        "TextBox.Text", "PasswordBox.Password", "ComboBox.Text", "ComboBox.SelectedItem", "ComboBox.SelectedIndex", "ComboBox.SelectedValue",
        "ListBox.SelectedItem", "ListBox.SelectedIndex", "ListBox.SelectedValue", "ListView.SelectedItem", "ListView.SelectedIndex",
        "TabControl.SelectedItem", "TabControl.SelectedIndex", "Expander.IsExpanded", "ListBoxItem.IsSelected", "TabItem.IsSelected",
    };

    [Fact]
    public void Twoway_bindings_never_target_read_only_view_model_properties()
    {
        var root = Root();
        var readOnly = ReadOnlyProperties(DesktopDll(root));
        var problems = new List<string>();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Nexus.Desktop", "Views"), "*.xaml", SearchOption.AllDirectories))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in doc.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    if (!TwoWayByDefault.Contains($"{element.Name.LocalName}.{attribute.Name.LocalName}")) continue;
                    var m = BindingRegex().Match(attribute.Value);
                    if (!m.Success || attribute.Value.Contains("Mode=OneWay", StringComparison.Ordinal) ||
                        attribute.Value.Contains("Mode=OneTime", StringComparison.Ordinal)) continue;
                    // DataContext = o tipo do DataTemplate mais próximo com DataType; templates internos sem DataType são pulados.
                    var template = element.Ancestors().FirstOrDefault(a => a.Name.LocalName == "DataTemplate");
                    var dataType = template?.Attribute("DataType")?.Value;
                    if (dataType is null) continue;
                    var type = TypeRegex().Match(dataType);
                    if (!type.Success) continue;
                    if (readOnly.TryGetValue(type.Groups[1].Value, out var props) && props.Contains(m.Groups[1].Value))
                        problems.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber} {element.Name.LocalName}.{attribute.Name.LocalName} → {type.Groups[1].Value}.{m.Groups[1].Value} (só leitura; use Mode=OneWay)");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Tela de progresso do Assistente: todo {Binding X} dentro do painel (DataContext = ProcessView), das linhas do feed e dos
    /// segmentos da barra existe no ViewModel certo; caminhos "ProcessView.X" existem em AssistantProcess. Nome errado aqui só
    /// apareceria no Windows, como um número parado ou uma linha vazia.
    /// </summary>
    [Fact]
    public void Assistant_progress_view_bindings_exist_on_their_view_models()
    {
        var root = Root();
        var all = AllProperties(DesktopDll(root));
        var file = Path.Combine(root, "src", "Nexus.Desktop", "Views", "Pages", "Assistant.xaml");
        var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var problems = new List<string>();
        var checkedCount = 0;

        void Check(XElement scope, string type)
        {
            foreach (var e in scope.DescendantsAndSelf())
            {
                // Modelo interno com outro DataType: conferido na sua própria vez.
                if (!ReferenceEquals(e, scope) && e.Name.LocalName == "DataTemplate" && e.Attribute("DataType") is not null) continue;
                if (e.Ancestors().TakeWhile(a => !ReferenceEquals(a, scope)).Any(a => a.Name.LocalName == "DataTemplate" && a.Attribute("DataType") is not null)) continue;
                foreach (var a in e.Attributes())
                {
                    var m = AnyBindingRegex().Match(a.Value);
                    if (!m.Success || a.Value.Contains("RelativeSource", StringComparison.Ordinal) || a.Value.Contains("ElementName", StringComparison.Ordinal)) continue;
                    if (a.Name.LocalName == "DataContext" && ReferenceEquals(e, scope)) continue;
                    checkedCount++;
                    var path = m.Groups[1].Value;
                    if (!all.TryGetValue(type, out var props) || !props.Contains(path))
                        problems.Add($"{((System.Xml.IXmlLineInfo)e).LineNumber} {e.Name.LocalName}.{a.Name.LocalName} → {type}.{path}");
                }
            }
        }

        var panel = doc.Descendants().Single(e => e.Attribute("DataContext")?.Value == "{Binding ProcessView}");
        Check(panel, "AssistantProcess");
        foreach (var t in doc.Descendants().Where(e => e.Name.LocalName == "DataTemplate" && e.Attribute("DataType")?.Value is "{x:Type vm:ProcessFeedRow}" or "{x:Type vm:ProcessSegment}"))
            Check(t, TypeRegex().Match(t.Attribute("DataType")!.Value).Groups[1].Value);
        foreach (var a in doc.Descendants().SelectMany(e => e.Attributes()))
        {
            var m = ProcessViewPathRegex().Match(a.Value);
            if (!m.Success) continue;
            checkedCount++;
            if (!all["AssistantProcess"].Contains(m.Groups[1].Value)) problems.Add($"ProcessView.{m.Groups[1].Value}");
        }
        Assert.True(all["AssistantPage"].Contains("ProcessView"), "AssistantPage.ProcessView sumiu");
        Assert.True(checkedCount > 30, $"só {checkedCount} ligações conferidas: a tela de progresso mudou de forma?");
        Assert.True(problems.Count == 0, "Ligações sem propriedade:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "DataTemplate" && e.Attribute(x + "Key")?.Value == "As.FeedRow");
    }

    [GeneratedRegex(@"^\{Binding\s+(?:Path=)?([A-Za-z_]\w*)\s*(?:,|\})")]
    private static partial Regex AnyBindingRegex();

    [GeneratedRegex(@"^\{Binding\s+(?:Path=)?ProcessView\.([A-Za-z_]\w*)\s*(?:,|\})")]
    private static partial Regex ProcessViewPathRegex();

    /// <summary>Tipo do ViewModel → todas as propriedades (inclui as herdadas de classes do próprio Desktop e as estáticas).</summary>
    private static Dictionary<string, HashSet<string>> AllProperties(string dll)
    {
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var own = new Dictionary<string, (HashSet<string> Props, string? Base)>(StringComparer.Ordinal);
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            if (md.GetString(t.Namespace) != "Nexus.Desktop.ViewModels") continue;
            var props = t.GetProperties().Select(ph => md.GetString(md.GetPropertyDefinition(ph).Name)).ToHashSet(StringComparer.Ordinal);
            string? baseName = null;
            if (!t.BaseType.IsNil && t.BaseType.Kind == HandleKind.TypeDefinition)
                baseName = md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)t.BaseType).Name);
            own[md.GetString(t.Name)] = (props, baseName);
        }
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (name, info) in own)
        {
            var props = new HashSet<string>(info.Props, StringComparer.Ordinal);
            for (var b = info.Base; b is not null && own.TryGetValue(b, out var bi); b = bi.Base) props.UnionWith(bi.Props);
            result[name] = props;
        }
        return result;
    }

    [GeneratedRegex(@"^\{Binding\s+(?:Path=)?([A-Za-z_]\w*)\s*(?:,|\})")]
    private static partial Regex BindingRegex();

    [GeneratedRegex(@"x:Type\s+vm:(\w+)")]
    private static partial Regex TypeRegex();

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string DesktopDll(string root)
    {
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var path = Path.Combine(root, "src", "Nexus.Desktop", "bin", config, "net10.0-windows", "Nexus.Desktop.dll");
        Assert.True(File.Exists(path), $"Nexus.Desktop.dll não encontrado em {path}: compile a solução inteira antes dos testes.");
        return path;
    }

    /// <summary>Tipo do ViewModel → propriedades sem setter público (inclui as herdadas de classes do próprio Desktop).</summary>
    private static Dictionary<string, HashSet<string>> ReadOnlyProperties(string dll)
    {
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var own = new Dictionary<string, (HashSet<string> Ro, HashSet<string> Rw, string? Base)>(StringComparer.Ordinal);
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            if (md.GetString(t.Namespace) != "Nexus.Desktop.ViewModels") continue;
            var ro = new HashSet<string>(StringComparer.Ordinal);
            var rw = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ph in t.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                var setter = p.GetAccessors().Setter;
                var publicSetter = !setter.IsNil && (md.GetMethodDefinition(setter).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                (publicSetter ? rw : ro).Add(md.GetString(p.Name));
            }
            string? baseName = null;
            if (!t.BaseType.IsNil && t.BaseType.Kind == HandleKind.TypeDefinition)
                baseName = md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)t.BaseType).Name);
            own[md.GetString(t.Name)] = (ro, rw, baseName);
        }
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (name, info) in own)
        {
            var ro = new HashSet<string>(info.Ro, StringComparer.Ordinal);
            var rw = new HashSet<string>(info.Rw, StringComparer.Ordinal);
            for (var b = info.Base; b is not null && own.TryGetValue(b, out var bi); b = bi.Base)
            {
                foreach (var p in bi.Ro) if (!rw.Contains(p)) ro.Add(p);
                rw.UnionWith(bi.Rw);
            }
            ro.ExceptWith(rw);
            result[name] = ro;
        }
        return result;
    }
}
