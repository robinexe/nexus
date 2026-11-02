using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nexus.Desktop.Views.Controls;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Tests;

/// <summary>
/// Design system v2 (docs/10-design-system.md): lógica pura dos controles (formatação, faixas de cor, frações, antes/depois,
/// anel), mapas da casca e a conferência de que toda chave {StaticResource} usada existe (chave inexistente só quebra em
/// execução, no Windows).
/// </summary>
[Trait("Category", "Unit")]
[Collection(LocalizationState.Name)] // Loc é estado global: quem formata texto da tela não roda junto com quem troca o idioma
public sealed partial class DesignSystemTests
{
    // ------------------------------------------------------------------ formatação

    [Theory]
    [InlineData(0d, "0 B")]
    [InlineData(512d, "512 B")]
    [InlineData(1536d, "1,5 KB")]
    [InlineData(3.4 * 1024 * 1024 * 1024, "3,4 GB")]
    [InlineData(120d * 1024 * 1024 * 1024, "120 GB")]
    [InlineData(-2048d, "-2,0 KB")]
    public void Bytes_use_base_1024_with_pt_br_decimals(double bytes, string expected) =>
        Assert.Equal(expected, DesignMath.Format(bytes, NumberKind.Bytes));

    [Theory]
    [InlineData(87.4, NumberKind.Percent, -1, "87%")]
    [InlineData(87.5, NumberKind.Percent, -1, "88%")]
    [InlineData(6d, NumberKind.Number, -1, "6")]
    [InlineData(1234.56, NumberKind.Number, -1, "1.234,6")]
    [InlineData(1234.56, NumberKind.Number, 0, "1.235")]
    [InlineData(3.14159, NumberKind.Number, 2, "3,14")]
    public void Numbers_and_percents_format_in_pt_br(double value, NumberKind kind, int decimals, string expected) =>
        Assert.Equal(expected, DesignMath.Format(value, kind, decimals));

    [Fact]
    public void Missing_values_show_a_dash()
    {
        Assert.Equal("—", DesignMath.Format(double.NaN, NumberKind.Number));
        Assert.Equal("—", DesignMath.Format(double.PositiveInfinity, NumberKind.Bytes));
    }

    // ------------------------------------------------------------------ faixas de cor

    [Theory]
    [InlineData(95, Tone.Good)]
    [InlineData(80, Tone.Warn)]
    [InlineData(65, Tone.Warn)]
    [InlineData(50, Tone.Bad)]
    [InlineData(10, Tone.Bad)]
    public void Higher_is_better_bands(double value, Tone expected) =>
        Assert.Equal(expected, DesignMath.ToneFor(value, warnAt: 80, badAt: 50, higherIsBetter: true));

    [Theory]
    [InlineData(40, Tone.Good)]
    [InlineData(85, Tone.Warn)]
    [InlineData(95, Tone.Bad)]
    public void Lower_is_better_bands(double value, Tone expected) =>
        Assert.Equal(expected, DesignMath.ToneFor(value, warnAt: 85, badAt: 95, higherIsBetter: false));

    [Fact]
    public void Invalid_value_is_neutral() =>
        Assert.Equal(Tone.Neutral, DesignMath.ToneFor(double.NaN, 80, 50, true));

    [Fact]
    public void Every_tone_maps_to_existing_theme_brushes_and_icons()
    {
        var keys = GlobalKeys();
        foreach (var tone in Enum.GetValues<Tone>())
        {
            Assert.Contains(DesignMath.BrushKey(tone), keys);
            Assert.Contains(DesignMath.SoftBrushKey(tone), keys);
            Assert.Contains(DesignMath.IconKey(tone), keys);
        }
    }

    // ------------------------------------------------------------------ frações, barras, diferença

    [Theory]
    [InlineData(50, 100, 0.5)]
    [InlineData(150, 100, 1)]
    [InlineData(-5, 100, 0)]
    [InlineData(5, 0, 0)]
    [InlineData(double.NaN, 100, 0)]
    public void Fraction_is_clamped(double value, double max, double expected) =>
        Assert.Equal(expected, DesignMath.Fraction(value, max), 6);

    [Fact]
    public void Bars_scale_to_the_larger_value()
    {
        Assert.Equal((0.5, 1d), DesignMath.BarScales(50, 100));
        Assert.Equal((1d, 0.25), DesignMath.BarScales(200, 50));
        Assert.Equal((0d, 0d), DesignMath.BarScales(0, 0));
        Assert.Equal((0d, 1d), DesignMath.BarScales(double.NaN, 3));
    }

    [Fact]
    public void Percent_change_needs_a_base()
    {
        Assert.Equal(25, DesignMath.PercentChange(80, 100)!.Value, 6);
        Assert.Equal(-50, DesignMath.PercentChange(100, 50)!.Value, 6);
        Assert.Null(DesignMath.PercentChange(0, 10));
    }

    [Fact]
    public void Delta_text_and_tone_follow_direction()
    {
        Assert.Equal(("+25%", Tone.Good), DesignMath.Delta(80, 100, NumberKind.Number, higherIsBetter: true));
        Assert.Equal(("+25%", Tone.Bad), DesignMath.Delta(80, 100, NumberKind.Number, higherIsBetter: false));
        Assert.Equal(("-50%", Tone.Good), DesignMath.Delta(100, 50, NumberKind.Number, higherIsBetter: false));
        Assert.Equal(("igual", Tone.Neutral), DesignMath.Delta(1000, 1002, NumberKind.Number, higherIsBetter: true));
        // Mudança pequena mas real não vira "+0%".
        Assert.Equal(("+1%", Tone.Good), DesignMath.Delta(100, 100.6, NumberKind.Number, higherIsBetter: true));
        // Sem base: diferença absoluta no formato do valor.
        Assert.Equal(("+2,0 GB", Tone.Good), DesignMath.Delta(0, 2d * 1024 * 1024 * 1024, NumberKind.Bytes, higherIsBetter: true));
        Assert.Equal(("—", Tone.Neutral), DesignMath.Delta(double.NaN, 1, NumberKind.Number, true));
    }

    // ------------------------------------------------------------------ nota e anel

    [Fact]
    public void Health_score_is_share_without_attention()
    {
        Assert.Null(DesignMath.HealthScore(0, 0));
        Assert.Equal(100, DesignMath.HealthScore(12, 0));
        Assert.Equal(83, DesignMath.HealthScore(12, 2));
        Assert.Equal(0, DesignMath.HealthScore(3, 5));
    }

    [Fact]
    public void Arc_starts_at_twelve_and_runs_clockwise()
    {
        static void Near((double X, double Y, bool L) a, double x, double y, bool large)
        {
            Assert.Equal(x, a.X, 6);
            Assert.Equal(y, a.Y, 6);
            Assert.Equal(large, a.L);
        }
        Near(DesignMath.ArcEnd(0, 10, 50, 50), 50, 40, false);
        Near(DesignMath.ArcEnd(0.25, 10, 50, 50), 60, 50, false);
        Near(DesignMath.ArcEnd(0.5, 10, 50, 50), 50, 60, false);
        Near(DesignMath.ArcEnd(0.75, 10, 50, 50), 40, 50, true);
        Near(DesignMath.ArcEnd(2, 10, 50, 50), 50, 40, true);
    }

    [Fact]
    public void Count_easing_starts_at_zero_ends_at_one_and_decelerates()
    {
        Assert.Equal(0, DesignMath.EaseOutCubic(0), 9);
        Assert.Equal(1, DesignMath.EaseOutCubic(1), 9);
        Assert.True(DesignMath.EaseOutCubic(0.5) > 0.5);
        Assert.Equal(10, DesignMath.Tween(0, 10, 1), 9);
        Assert.Equal(7, DesignMath.Tween(double.NaN, 7, 0), 9);
    }

    // ------------------------------------------------------------------ mapas da casca

    [Fact]
    public void Every_page_has_its_own_icon_and_illustration_and_they_exist()
    {
        var keys = GlobalKeys();
        foreach (var page in PageTypes())
            Assert.True(ShellMap.IconFor(page) != ShellMap.DefaultIcon, $"Página {page} sem ícone em ShellMap.");
        foreach (var page in ShellMap.KnownPages)
        {
            Assert.Contains(ShellMap.IconFor(page), keys);
            Assert.Contains(ShellMap.IllustrationFor(page), keys);
        }
        Assert.Contains(ShellMap.DefaultIcon, keys);
        Assert.Contains(ShellMap.DefaultIllustration, keys);
    }

    [Theory]
    [InlineData("mem.channels", "MemoryLabPage")]
    [InlineData("gpu.hags", "GpuLabPage")]
    [InlineData("disk.system-free", "CleaningPage")]
    [InlineData("sec.hvci", "SecurityPage")]
    [InlineData("power.scheme", "CpuLabPage")]
    [InlineData("win.gamemode", "CatalogPage")]
    [InlineData("owner.rebar", "GpuLabPage")]
    [InlineData("owner.pcie", "GpuLabPage")]
    [InlineData("owner.mux", "GpuLabPage")]
    [InlineData("owner.xmp", "MemoryLabPage")]
    [InlineData("startup.count", "StartupPage")]
    [InlineData("proc.elevated", "DiagnosticsPage")]
    [InlineData(null, "DiagnosticsPage")]
    public void Findings_open_the_page_that_solves_them(string? id, string page)
    {
        Assert.Equal(page, ShellMap.PageForFinding(id));
        Assert.NotEqual("Abrir", ShellMap.ActionFor(page));
    }

    [Fact]
    public void Groups_keep_their_order_and_unknown_go_last()
    {
        Assert.True(ShellMap.GroupRank("Meu PC") < ShellMap.GroupRank("Limpar"));
        Assert.True(ShellMap.GroupRank("Limpar") < ShellMap.GroupRank("Registro"));
        Assert.Equal(ShellMap.GroupOrder.Count, ShellMap.GroupRank("Grupo novo"));
    }

    // ------------------------------------------------------------------ chaves de recurso

    /// <summary>
    /// Toda {StaticResource X} dos arquivos do design system e de todas as páginas (Views/**) existe num dicionário global
    /// (Themes/*, Assets/Illustrations/*) ou no próprio arquivo (DataTemplate.Resources, Window.Resources).
    /// </summary>
    [Fact]
    public void Every_static_resource_used_by_the_design_system_exists()
    {
        var root = Root();
        var desktop = Path.Combine(root, "src", "Nexus.Desktop");
        var global = GlobalKeys();
        var files = Directory.EnumerateFiles(Path.Combine(desktop, "Themes"), "*.xaml")
            .Concat(Directory.EnumerateFiles(Path.Combine(desktop, "Assets", "Illustrations"), "*.xaml"))
            .Concat(Directory.EnumerateFiles(Path.Combine(desktop, "Views"), "*.xaml", SearchOption.AllDirectories))
            .Append(Path.Combine(desktop, "MainWindow.xaml"));
        var problems = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var local = KeysIn(text);
            foreach (Match m in StaticResourceRegex().Matches(text))
            {
                var key = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
                if (key.StartsWith("{x:Static", StringComparison.Ordinal)) continue;
                if (!global.Contains(key) && !local.Contains(key)) problems.Add($"{Path.GetFileName(file)}: {key}");
            }
        }
        Assert.True(problems.Count == 0, "Chaves inexistentes:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Distinct()));
    }

    [Fact]
    public void Illustration_and_icon_keys_are_unique_and_well_formed()
    {
        var root = Root();
        var desktop = Path.Combine(root, "src", "Nexus.Desktop");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(desktop, "Assets", "Illustrations"), "*.xaml")
                     .Append(Path.Combine(desktop, "Themes", "Icons.xaml")))
        {
            var doc = XDocument.Load(file);
            foreach (var e in doc.Root!.Elements())
            {
                var key = e.Attribute(x + "Key")?.Value;
                Assert.False(key is null, $"{Path.GetFileName(file)}: elemento sem x:Key");
                Assert.True(seen.Add(key!), $"Chave repetida: {key}");
                Assert.True(key!.StartsWith("Illo.", StringComparison.Ordinal) || key.StartsWith("Icon.", StringComparison.Ordinal), key);
                // Mini-linguagem de caminho: só comandos e números (nada de texto solto que o WPF recusaria em execução).
                var paths = e.Name.LocalName == "Geometry" ? [e.Value] : e.Descendants().Select(d => d.Attribute("Geometry")?.Value).OfType<string>();
                foreach (var p in paths)
                    Assert.True(PathDataRegex().IsMatch(p), $"{key}: caminho inválido '{p}'");
            }
        }
        // Uma ilustração por módulo pedido.
        foreach (var k in (string[])["Illo.Cpu", "Illo.Gpu", "Illo.Ram", "Illo.Disk", "Illo.Cleaning", "Illo.Programs", "Illo.Startup",
                     "Illo.Services", "Illo.Privacy", "Illo.Security", "Illo.Scanner", "Illo.Network", "Illo.Drivers", "Illo.Measure",
                     "Illo.Monitor", "Illo.Automation", "Illo.Assistant", "Illo.MemoryDoctor", "Illo.Settings", "Illo.Pc"])
            Assert.Contains(k, seen);
    }

    // ------------------------------------------------------------------ tela de progresso (Assistente)

    /// <summary>
    /// Componentes da tela de progresso: existem no Desktop com as propriedades usadas no XAML, estão no manual do design
    /// system e todo laço infinito de animação nos controles respeita "Mostrar animações no Windows" (ShellMotion.Enabled).
    /// </summary>
    [Fact]
    public void Progress_view_components_exist_are_documented_and_respect_reduced_motion()
    {
        var root = Root();
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var dll = Path.Combine(root, "src", "Nexus.Desktop", "bin", config, "net10.0-windows", "Nexus.Desktop.dll");
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var fields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            if (md.GetString(t.Namespace) != "Nexus.Desktop.Views.Controls") continue;
            fields[md.GetString(t.Name)] = t.GetFields().Select(f => md.GetString(md.GetFieldDefinition(f).Name)).ToHashSet(StringComparer.Ordinal);
        }
        foreach (var (type, dps) in new[]
                 {
                     ("ProcessRing", new[] { "ValueProperty", "IsActiveProperty", "ToneProperty", "ThicknessProperty" }),
                     ("ScanlineOverlay", new[] { "IsActiveProperty" }),
                     ("Pulse", new[] { "IsOnProperty" }),
                 })
        {
            Assert.True(fields.ContainsKey(type), $"{type} não existe em Views/Controls");
            foreach (var dp in dps) Assert.Contains(dp, fields[type]);
        }

        var manual = File.ReadAllText(Path.Combine(root, "docs", "10-design-system.md"));
        foreach (var name in new[] { "ProcessRing", "ScanlineOverlay", "Pulse.IsOn" }) Assert.Contains(name, manual, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Nexus.Desktop", "Views", "Controls"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            var loops = Regex.Count(text, @"RepeatBehavior\.Forever");
            if (loops == 0) continue;
            Assert.True(Regex.Count(text, @"ShellMotion\.Enabled") >= 1, $"{Path.GetFileName(file)}: animação em laço sem conferir ShellMotion.Enabled");
        }
        var visuals = File.ReadAllText(Path.Combine(root, "src", "Nexus.Desktop", "Views", "Controls", "ProcessVisuals.cs"));
        // Cada controle com laço decide "quero animar" olhando as animações do Windows.
        Assert.True(Regex.Count(visuals, @"ShellMotion\.Enabled") >= 3, "ProcessRing, ScanlineOverlay e Pulse devem conferir ShellMotion.Enabled antes de animar");
    }

    // ------------------------------------------------------------------ apoio

    [GeneratedRegex(@"\{StaticResource\s+(?:ResourceKey=)?([^\s,}]+)\s*\}|<StaticResource\s+ResourceKey=""([^""]+)""")]
    private static partial Regex StaticResourceRegex();

    [GeneratedRegex(@"x:Key=""([^""]+)""")]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"^(?=\s*[Mm])\s*(?:(?:[MmLlHhVvCcSsQqTtAa]\s*)?-?(?:\d+\.?\d*|\.\d+)(?:[eE]-?\d+)?\s*,?\s*|[Zz]\s*)+$")]
    private static partial Regex PathDataRegex();

    private static HashSet<string> KeysIn(string xaml) =>
        new(KeyRegex().Matches(xaml).Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    /// <summary>Chaves dos dicionários globais (App.xaml): Themes/* e Assets/Illustrations/*.</summary>
    private static HashSet<string> GlobalKeys()
    {
        var desktop = Path.Combine(Root(), "src", "Nexus.Desktop");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(desktop, "Themes"), "*.xaml")
                     .Concat(Directory.EnumerateFiles(Path.Combine(desktop, "Assets", "Illustrations"), "*.xaml")))
            keys.UnionWith(KeysIn(File.ReadAllText(file)));
        return keys;
    }

    /// <summary>Páginas da navegação: tipos de Nexus.Desktop.ViewModels que herdam de PageViewModel ou ModulePage.</summary>
    private static List<string> PageTypes()
    {
        var root = Root();
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var dll = Path.Combine(root, "src", "Nexus.Desktop", "bin", config, "net10.0-windows", "Nexus.Desktop.dll");
        Assert.True(File.Exists(dll), $"Nexus.Desktop.dll não encontrado em {dll}: compile a solução inteira antes dos testes.");
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var pages = new List<string>();
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            if (md.GetString(t.Namespace) != "Nexus.Desktop.ViewModels" || t.BaseType.IsNil || t.BaseType.Kind != HandleKind.TypeDefinition) continue;
            if ((t.Attributes & System.Reflection.TypeAttributes.Abstract) != 0) continue;
            var baseName = md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)t.BaseType).Name);
            if (baseName is "PageViewModel" or "ModulePage") pages.Add(md.GetString(t.Name));
        }
        Assert.NotEmpty(pages);
        return pages;
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
