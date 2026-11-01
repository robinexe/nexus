using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nexus.Tests;

/// <summary>
/// Fiação dos XAML: todo Command ligado aponta para um ICommand que existe no ViewModel do contexto e que recebe valor; toda
/// navegação (ShellCommands.Navigate, FindPage, Target = "XPage") cai numa página que existe; toda chave "Icon.*"/"Illo.*" escrita
/// em C# existe nos dicionários. Chave/propriedade errada no XAML só quebra em execução, no Windows, em silêncio: o botão some
/// ou não faz nada.
/// </summary>
public sealed partial class WiringTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>XAML das páginas e da casca, com o tipo do contexto da raiz quando não vem de um DataTemplate (MainWindow).</summary>
    private static IEnumerable<(string File, XDocument Doc, string? RootType)> XamlFiles()
    {
        var desktop = Path.Combine(Wiring.Root, "src", "Nexus.Desktop");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(desktop, "Views"), "*.xaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            yield return (file, XDocument.Load(file, LoadOptions.SetLineInfo), null);
        yield return (Path.Combine(desktop, "MainWindow.xaml"), XDocument.Load(Path.Combine(desktop, "MainWindow.xaml"), LoadOptions.SetLineInfo), "MainViewModel");
    }

    /// <summary>
    /// Todo atributo *Command="{Binding Caminho}" resolve, no tipo do contexto (DataTemplate DataType, ItemsSource do dono do
    /// modelo, DataContext="{Binding}", RelativeSource AncestorType), para uma propriedade de tipo comando, e essa propriedade
    /// recebe valor em algum lugar (inicializador, construtor ou atribuição) — senão o botão fica morto.
    /// </summary>
    [Fact]
    public void Every_bound_command_exists_on_the_view_model_and_gets_a_value()
    {
        var withValue = Wiring.PropertiesWithValue(Wiring.DesktopDll);
        var problems = new List<string>();
        int resolved = 0, total = 0;
        var unresolved = new List<string>();
        foreach (var (file, doc, rootType) in XamlFiles())
        {
            var ctx = new XamlContext(doc, rootType);
            foreach (var element in doc.Descendants())
                foreach (var attribute in element.Attributes())
                {
                    if (!attribute.Name.LocalName.EndsWith("Command", StringComparison.Ordinal) || attribute.Name.Namespace != XNamespace.None) continue;
                    var b = BindingPathRegex().Match(attribute.Value);
                    if (!b.Success) continue;
                    total++;
                    var where = $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber} {element.Name.LocalName}.{attribute.Name.LocalName}";
                    var path = b.Groups[1].Value;
                    if (path.Length == 0) continue;
                    var ancestor = AncestorTypeRegex().Match(attribute.Value);
                    if (attribute.Value.Contains("ElementName", StringComparison.Ordinal)) { unresolved.Add(where + " (ElementName)"); continue; }
                    var targets = new List<(string? Type, string Path)>();
                    if (ancestor.Success)
                    {
                        var owners = ctx.Ancestors(element, ancestor.Groups[1].Value).ToList();
                        if (owners.Count == 0) { unresolved.Add(where + " (ancestral não achado)"); continue; }
                        foreach (var o in owners)
                        {
                            if (path.StartsWith("DataContext.", StringComparison.Ordinal)) targets.Add((ctx.ContextOf(o), path["DataContext.".Length..]));
                            // Tag="{Binding XCommand}" no ItemsControl: o comando vem do contexto dele (ex.: Remover de cada linha).
                            else if (path == "Tag" && o.Attribute("Tag")?.Value is { } tag && BindingPathRegex().Match(tag) is { Success: true } tm)
                                targets.Add((ctx.ContextOf(o), tm.Groups[1].Value));
                        }
                        if (targets.Count == 0) { unresolved.Add(where + " (RelativeSource sem DataContext/Tag)"); continue; }
                    }
                    else targets.Add((ctx.ContextOf(element), path));
                    foreach (var (type, targetPath) in targets)
                    {
                        if (type is null) { unresolved.Add(where + " (contexto desconhecido)"); continue; }
                        path = targetPath;
                        var (owner, property, propertyType) = ResolvePath(type, path);
                        if (propertyType is not null && propertyType.StartsWith('?')) { unresolved.Add(where + " (tipo externo)"); continue; }
                        if (propertyType is null)
                        {
                            problems.Add($"{where} → {type}.{path}: propriedade não existe (o botão nunca funciona)");
                            continue;
                        }
                        resolved++;
                        if (!propertyType.Contains("Command", StringComparison.Ordinal))
                            problems.Add($"{where} → {type}.{path}: é {propertyType}, não um comando");
                        else if (!withValue.Contains($"{owner}.{property}") && Wiring.Types.TryGetValue(owner!, out var info) && info.Namespace.StartsWith("Nexus.Desktop", StringComparison.Ordinal))
                            problems.Add($"{where} → {owner}.{property}: nunca recebe valor (fica nulo: o botão não faz nada)");
                    }
                }
        }
        if (Environment.GetEnvironmentVariable("NEXUS_WIRING_DUMP") is { Length: > 0 } dump)
            File.WriteAllLines(dump + ".xaml", [$"{resolved}/{total}", .. unresolved]);
        Assert.True(total > 200, $"Só {total} comandos achados nos XAML: o leitor quebrou?");
        Assert.True(resolved >= total * 0.95, $"Só {resolved} de {total} comandos com contexto resolvido. Sem contexto:{Environment.NewLine}{string.Join(Environment.NewLine, unresolved)}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Segue "A.B.C" a partir do tipo; devolve o tipo dono da última propriedade, o nome e o tipo dela (null = não existe).</summary>
    private static (string? Owner, string? Property, string? Type) ResolvePath(string type, string path)
    {
        var current = type;
        string? owner = null, property = null;
        foreach (var segment in path.Split('.'))
        {
            if (segment.Contains('[', StringComparison.Ordinal)) return (null, null, "?");
            owner = OwnerOf(current, segment);
            property = segment;
            var next = Wiring.PropertyType(current, segment);
            if (next is null)
                return Wiring.TypeExists(current) ? (owner, property, null) : (owner, property, "?externo"); // tipo de fora do produto: não dá para conferir
            current = next;
        }
        return (owner, property, current);
    }

    private static string? OwnerOf(string type, string property)
    {
        for (var name = type; name is not null && Wiring.Types.TryGetValue(Wiring.StripGenerics(name), out var t); name = t.BaseName)
            if (t.Properties.ContainsKey(property)) return t.Name;
        return null;
    }

    // ================================================================== (d) navegação e chaves de ícone

    /// <summary>Páginas da navegação: os tipos das propriedades listadas em "Pages = [...]" do MainViewModel.</summary>
    private static HashSet<string> PageTypeNames()
    {
        var source = File.ReadAllText(Path.Combine(Wiring.Root, "src", "Nexus.Desktop", "ViewModels", "MainViewModel.cs"));
        var list = PagesListRegex().Match(source);
        Assert.True(list.Success, "MainViewModel: \"Pages = [...]\" não encontrado.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in list.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var type = Wiring.PropertyType("MainViewModel", p);
            Assert.True(type is not null, $"MainViewModel.{p} não existe.");
            names.Add(type!);
        }
        Assert.True(names.Count >= 20);
        return names;
    }

    [Fact]
    public void Every_navigation_target_is_a_page_in_the_shell()
    {
        var pages = PageTypeNames();
        var problems = new List<string>();
        // XAML: Command/SecondaryCommand = ShellCommands.Navigate com CommandParameter/SecondaryCommandParameter literal.
        foreach (var (file, doc, _) in XamlFiles())
            foreach (var element in doc.Descendants())
                foreach (var (command, parameter) in new[] { ("Command", "CommandParameter"), ("SecondaryCommand", "SecondaryCommandParameter") })
                {
                    if (element.Attribute(command)?.Value.Contains("ShellCommands.Navigate", StringComparison.Ordinal) != true) continue;
                    var target = element.Attribute(parameter)?.Value;
                    if (target is null || target.StartsWith('{')) continue; // ligado a dados: conferido pelas strings do C# abaixo
                    if (!pages.Contains(target))
                        problems.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber}: {parameter}=\"{target}\" não é uma página da navegação (o botão fica desabilitado)");
                }
        // C#: toda string "AlgoPage" dos binários do Desktop (Target = "CpuLabPage", ShellMap, PageForFinding...) é uma página.
        foreach (var s in Wiring.Literals(Wiring.DesktopDll).Distinct(StringComparer.Ordinal))
            if (PageNameRegex().IsMatch(s) && !pages.Contains(s) && s is not ("ModulePage" or "PageViewModel" or "NomeDoTipoDaPagina" or "SelectedPage"))
                problems.Add($"Nexus.Desktop.dll: \"{s}\" parece nome de página, mas não está em MainViewModel.Pages");
        // C#: FindPage(typeof(X)).
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Wiring.Root, "src", "Nexus.Desktop"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in TypeofPageRegex().Matches(text))
                if (!pages.Contains(m.Groups[1].Value) && m.Groups[1].Value is not ("ModulePage" or "PageViewModel"))
                    problems.Add($"{Path.GetFileName(file)}:{Wiring.LineOf(text, m.Index)}: typeof({m.Groups[1].Value}) não é uma página da navegação (FindPage devolve null)");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>"Abrir outra página" do diagnóstico de memória: todo destino escrito em MemoryDoctor.cs tem tradução na página.</summary>
    [Fact]
    public void Memory_doctor_navigate_targets_open_a_page()
    {
        var doctor = File.ReadAllText(Path.Combine(Wiring.Root, "src", "Nexus.Lab.Memory", "Problems", "MemoryDoctor.cs"));
        var page = File.ReadAllText(Path.Combine(Wiring.Root, "src", "Nexus.Desktop", "ViewModels", "Modules", "MemoryDoctorPage.cs"));
        var targets = DoctorNavigateRegex().Matches(doctor).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(targets);
        var map = DoctorTargetMapRegex().Matches(page).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        var pages = PageTypeNames();
        foreach (var t in targets)
        {
            Assert.True(map.TryGetValue(t, out var type), $"MemoryDoctor: destino \"{t}\" sem tradução em MemoryDoctorPage.ApplyFixAsync (o botão Abrir não faz nada).");
            Assert.Contains(type!, pages);
        }
    }

    /// <summary>Toda string "Icon.X"/"Illo.X" do C# (chaves montadas por ViewModels) existe em Themes/* ou Assets/Illustrations/*.</summary>
    [Fact]
    public void Every_icon_and_illustration_key_written_in_code_exists()
    {
        var desktop = Path.Combine(Wiring.Root, "src", "Nexus.Desktop");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(desktop, "Themes"), "*.xaml")
                     .Concat(Directory.EnumerateFiles(Path.Combine(desktop, "Assets", "Illustrations"), "*.xaml")))
            keys.UnionWith(XKeyRegex().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value));
        var problems = new List<string>();
        var count = 0;
        foreach (var dll in Wiring.ProductAssemblies())
            foreach (var s in Wiring.Literals(dll).Distinct(StringComparer.Ordinal))
            {
                if (!IconKeyRegex().IsMatch(s)) continue;
                count++;
                if (!keys.Contains(s)) problems.Add($"{Path.GetFileName(dll)}: \"{s}\" não existe nos dicionários (o ícone some em execução)");
            }
        Assert.True(count >= 30, $"Só {count} chaves de ícone no código?");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>(e) Toda página tem pelo menos um botão ligado (comando do ViewModel ou navegação).</summary>
    [Fact]
    public void Every_page_has_at_least_one_wired_button()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Wiring.Root, "src", "Nexus.Desktop", "Views", "Pages"), "*.xaml"))
        {
            var doc = XDocument.Load(file);
            var commands = doc.Descendants().SelectMany(e => e.Attributes())
                .Count(a => a.Name.LocalName.EndsWith("Command", StringComparison.Ordinal) && a.Value.StartsWith('{'));
            if (commands == 0) problems.Add($"{Path.GetFileName(file)}: nenhum botão ligado");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ================================================================== contexto de dados nos XAML

    private sealed class XamlContext(XDocument doc, string? rootType)
    {
        private readonly Dictionary<string, XElement> _keyed = doc.Descendants().Where(e => e.Attribute(X + "Key") is not null)
            .GroupBy(e => e.Attribute(X + "Key")!.Value).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        /// <summary>Tipo do DataContext em que as ligações do elemento são avaliadas.</summary>
        public string? ContextOf(XElement element, int depth = 0)
        {
            if (depth > 12) return null;
            for (var a = element; a is not null; a = a.Parent)
            {
                // DataContext="{Binding P}" no próprio elemento ou num ancestral muda o contexto dali para dentro.
                if (a.Attribute("DataContext")?.Value is { } dc && BindingPathRegex().Match(dc) is { Success: true } m)
                    return a.Parent is null ? null : Follow(ContextOf(a.Parent, depth + 1), m.Groups[1].Value);
                if (a.Name.LocalName is "ControlTemplate" or "Style") return null;
                if (a.Name.LocalName == "DataTemplate")
                {
                    if (a.Attribute("DataType")?.Value is { } dt && DataTypeRegex().Match(dt) is { Success: true } t) return t.Groups[1].Value;
                    return TemplateContext(a, depth);
                }
            }
            return rootType;
        }

        /// <summary>Contexto de um DataTemplate sem DataType: o item do ItemsSource (ou o Content) de quem usa o modelo.</summary>
        private string? TemplateContext(XElement template, int depth)
        {
            foreach (var (owner, viaItems) in TemplateOwners(template))
            {
                var source = owner.Attribute(viaItems ? "ItemsSource" : "Content")?.Value;
                if (source is null || BindingPathRegex().Match(source) is not { Success: true } m) continue;
                var collection = Follow(ContextOf(owner, depth + 1), m.Groups[1].Value);
                if (collection is null) continue;
                return viaItems ? Wiring.ElementType(collection) : collection;
            }
            return null;
        }

        /// <summary>Quem usa o modelo: o elemento do "X.ItemTemplate"/"X.ContentTemplate" em volta, ou quem o cita por chave.</summary>
        public IEnumerable<(XElement Owner, bool ViaItems)> TemplateOwners(XElement template)
        {
            if (template.Parent is { } p && p.Name.LocalName.Contains('.', StringComparison.Ordinal) && p.Parent is { } owner)
            {
                if (p.Name.LocalName.EndsWith(".ItemTemplate", StringComparison.Ordinal)) yield return (owner, true);
                else if (p.Name.LocalName.EndsWith(".ContentTemplate", StringComparison.Ordinal)) yield return (owner, false);
            }
            if (template.Attribute(X + "Key")?.Value is { } key)
                foreach (var e in doc.Descendants())
                {
                    if (e.Attribute("ItemTemplate")?.Value is { } it && it.Contains("StaticResource " + key + "}", StringComparison.Ordinal)) yield return (e, true);
                    if (e.Attribute("ContentTemplate")?.Value is { } ct && ct.Contains("StaticResource " + key + "}", StringComparison.Ordinal)) yield return (e, false);
                }
        }

        /// <summary>
        /// Ancestrais visuais do tipo pedido (RelativeSource AncestorType), atravessando modelos por quem os usa (um modelo por
        /// chave pode ser usado por vários ItemsControl: devolve todos).
        /// </summary>
        public IEnumerable<XElement> Ancestors(XElement element, string ancestorType, int depth = 0)
        {
            if (depth > 6) yield break;
            for (XElement? a = element.Parent; a is not null; a = a.Parent)
            {
                if (Matches(a, ancestorType)) { yield return a; yield break; }
                if (a.Name.LocalName == "DataTemplate")
                {
                    foreach (var (owner, _) in TemplateOwners(a))
                    {
                        if (Matches(owner, ancestorType)) yield return owner;
                        else foreach (var up in Ancestors(owner, ancestorType, depth + 1)) yield return up;
                    }
                    yield break;
                }
            }
        }

        private static bool Matches(XElement e, string ancestorType) =>
            e.Name.LocalName == ancestorType ||
            (ancestorType == "ItemsControl" && e.Name.LocalName is "ListBox" or "ListView" or "ComboBox" or "DataGrid" or "TabControl");

        private static string? Follow(string? type, string path)
        {
            if (type is null) return null;
            if (path is "" or ".") return type;
            var current = type;
            foreach (var segment in path.Split('.'))
            {
                current = Wiring.PropertyType(current, segment);
                if (current is null) return null;
            }
            return current;
        }
    }

    [GeneratedRegex(@"\A\{Binding\s*(?:Path=)?([A-Za-z_][\w.]*)?\s*(?:,|\})")]
    private static partial Regex BindingPathRegex();

    [GeneratedRegex(@"AncestorType=\{?(?:x:Type\s+)?(?:\w+:)?(\w+)\}?")]
    private static partial Regex AncestorTypeRegex();

    [GeneratedRegex(@"x:Type\s+\w+:(\w+)")]
    private static partial Regex DataTypeRegex();

    [GeneratedRegex(@"Pages\s*=\s*\[([^\]]+)\]")]
    private static partial Regex PagesListRegex();

    [GeneratedRegex(@"\A[A-Z][A-Za-z]+Page\z")]
    private static partial Regex PageNameRegex();

    [GeneratedRegex(@"typeof\((\w+Page)\)")]
    private static partial Regex TypeofPageRegex();

    [GeneratedRegex(@"Navigate\(""[^""]+"",\s*FixTerm\.\w+,\s*""[^""]*"",\s*""([^""]+)""")]
    private static partial Regex DoctorNavigateRegex();

    [GeneratedRegex(@"""(\w+)""\s*=>\s*typeof\((\w+)\)")]
    private static partial Regex DoctorTargetMapRegex();

    [GeneratedRegex(@"x:Key=""([^""]+)""")]
    private static partial Regex XKeyRegex();

    [GeneratedRegex(@"\AI(?:con|llo)\.[A-Z]\w*\z")]
    private static partial Regex IconKeyRegex();
}
