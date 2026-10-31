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
