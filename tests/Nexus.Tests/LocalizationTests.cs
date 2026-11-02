using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nexus.Tests;

/// <summary>
/// Idiomas da interface (pt-BR padrão, en, es; docs/12-idiomas.md): toda chave existe nos três arquivos, com os mesmos
/// marcadores {n}; toda chave usada no XAML/código existe; as páginas já traduzidas não têm texto fixo em português; o
/// ResourceManager acha as traduções (satélites) e a escolha do idioma segue a regra (--lang &gt; Ajustes &gt; Windows &gt; pt-BR).
/// </summary>
[Trait("Category", "Unit")]
[Collection(LocalizationState.Name)] // Loc é estado global: quem formata texto da tela não roda junto com quem troca o idioma
public sealed partial class LocalizationTests
{
    /// <summary>
    /// XAML ainda sem tradução (texto fixo em pt-BR). Cada página traduzida sai desta lista; o teste falha se uma página
    /// listada já não tiver texto fixo (para a lista não mentir) ou se uma página fora dela tiver.
    /// </summary>
    private static readonly HashSet<string> XamlNotYetTranslated = new(StringComparer.Ordinal)
    {
    };

    /// <summary>Textos fixos permitidos em XAML traduzido: marcas e nomes próprios iguais em todo idioma.</summary>
    private static readonly HashSet<string> XamlLiteralExemptions = new(StringComparer.Ordinal)
    {
        "NEXUS Performance Lab", "NEXUS", "NVIDIA", "AMD", "Intel", "PresentMon", "Microsoft Defender", "Windows Update",
        // Unidades e siglas iguais em todo idioma.
        "Microsoft", "Windows", "Steam", "Epic", "FPS", "GPU", "CPU", "RAM", "VRAM", "Hz", "MHz", "GHz", "ms", "ns", "GB", "MB", "GB/s", "W", "°C",
    };

    /// <summary>Atributos que mostram texto ao usuário (mesma lista da ferramenta de conversão).</summary>
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Title", "Detail", "Header", "Label", "AutomationProperties.Name", "AutomationProperties.HelpText", "ToolTip",
        "ActionText", "SecondaryText", "Caption", "Prefix", "BeforeLabel", "AfterLabel", "ValueText", "Subtitle", "Description",
        "ToolTipService.ToolTip",
    };

    private static readonly string[] Languages = ["pt-BR", "en", "es"];

    [Fact]
    public void Every_key_exists_in_all_languages_with_text()
    {
        var all = AllResources();
        var pt = all["pt-BR"];
        Assert.True(pt.Count >= 100, $"Strings.resx com só {pt.Count} chaves");
        var problems = new List<string>();
        foreach (var lang in Languages.Skip(1))
        {
            problems.AddRange(pt.Keys.Except(all[lang].Keys).Select(k => $"{lang}: falta '{k}'"));
            problems.AddRange(all[lang].Keys.Except(pt.Keys).Select(k => $"{lang}: sobra '{k}' (não existe no pt-BR)"));
        }
        foreach (var (lang, d) in all)
            problems.AddRange(d.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => $"{lang}: '{kv.Key}' vazio"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Placeholders_match_across_languages_and_format_without_error()
    {
        var all = AllResources();
        var problems = new List<string>();
        foreach (var (key, pt) in all["pt-BR"])
        {
            var expected = Placeholders(pt);
            foreach (var lang in Languages)
            {
                if (!all[lang].TryGetValue(key, out var value)) continue;
                var got = Placeholders(value);
                if (!got.SequenceEqual(expected))
                    problems.Add($"{lang} '{key}': marcadores [{string.Join(",", got)}] ≠ pt-BR [{string.Join(",", expected)}]");
                if (expected.Count == 0) continue;
                // Mesma checagem de sintaxe que Loc.F faz em execução (texto como argumento ignora o formato).
                try { _ = string.Format(CultureInfo.GetCultureInfo(lang), value, Enumerable.Repeat<object?>("x", expected.Max() + 1).ToArray()); }
                catch (FormatException ex) { problems.Add($"{lang} '{key}': {ex.Message}"); }
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_key_used_in_xaml_and_code_exists()
    {
        var keys = AllResources()["pt-BR"].Keys.ToHashSet(StringComparer.Ordinal);
        var used = UsedKeys();
        var missing = used.Where(u => !keys.Contains(u.Key)).Select(u => $"{u.Where}: '{u.Key}' não existe em Strings.resx").ToList();
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
        // Chave sem uso é tradução morta: apague dos três arquivos.
        var unused = keys.Except(used.Select(u => u.Key)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unused.Count == 0, "Chaves sem uso: " + string.Join(", ", unused));
    }

    [Fact]
    public void Translated_xaml_has_no_fixed_user_text()
    {
        var problems = new List<string>();
        var stillListed = new List<string>();
        foreach (var file in XamlFiles())
        {
            var name = Path.GetRelativePath(Path.Combine(Root(), "src", "Nexus.Desktop"), file).Replace('\\', '/');
            var literals = FixedTexts(file).ToList();
            if (XamlNotYetTranslated.Contains(name))
            {
                if (literals.Count == 0) stillListed.Add(name);
                continue;
            }
            problems.AddRange(literals.Select(l => $"{name}:{l.Line} {l.Attribute}=\"{l.Value}\" (use {{l:T Chave}})"));
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(stillListed.Count == 0, "Já traduzidas, tire de XamlNotYetTranslated: " + string.Join(", ", stillListed));
    }

    [Fact]
    public void Resource_manager_finds_each_language_and_falls_back_to_pt_br()
    {
        var pt = AllResources()["pt-BR"];
        Assert.Equal(pt["Shell.Fechar"], Loc.Get("Shell.Fechar", CultureInfo.GetCultureInfo("pt-BR")));
        Assert.Equal("Close", Loc.Get("Shell.Fechar", CultureInfo.GetCultureInfo("en")));
        Assert.Equal("Cerrar", Loc.Get("Shell.Fechar", CultureInfo.GetCultureInfo("es")));
        // Variante regional usa o idioma; idioma sem tradução usa o pt-BR (neutro).
        Assert.Equal("Close", Loc.Get("Shell.Fechar", CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal(pt["Shell.Fechar"], Loc.Get("Shell.Fechar", CultureInfo.GetCultureInfo("fr-FR")));
        // Chave inexistente aparece como a própria chave (visível na tela, nunca derruba a janela).
        Assert.Equal("Nao.Existe", Loc.Get("Nao.Existe", CultureInfo.GetCultureInfo("en")));
    }

    [Fact]
    public void Loc_formats_in_the_chosen_language_and_survives_bad_formats()
    {
        try
        {
            Loc.Use("en");
            Assert.Equal("en", Loc.Language);
            Assert.Equal("Coordination: ok", Loc.F("Shell.CoordenacaoN", "ok"));
            Loc.Use("es");
            Assert.Equal("Coordinación: ok", Loc.F("Shell.CoordenacaoN", "ok"));
            Loc.Use("xx");
            Assert.Equal("pt-BR", Loc.Language);
            // Menos argumentos que marcadores: devolve o texto cru em vez de lançar.
            Assert.Contains("{0}", Loc.F("Shell.CoordenacaoN"), StringComparison.Ordinal);
        }
        finally { Loc.Use("pt-BR"); }
    }

    [Fact]
    public void Numbers_on_screen_follow_the_interface_language()
    {
        try
        {
            Assert.Equal("3,4 GB", Nexus.Desktop.Views.Controls.DesignMath.Bytes(3.4 * 1024 * 1024 * 1024));
            Loc.Use("en");
            Assert.Equal("3.4 GB", Nexus.Desktop.Views.Controls.DesignMath.Bytes(3.4 * 1024 * 1024 * 1024));
            Assert.Equal("1,234", Nexus.Desktop.Views.Controls.DesignMath.Format(1234, Nexus.Desktop.Views.Controls.NumberKind.Number, 0));
            Loc.Use("es");
            Assert.Equal("3,4 GB", Nexus.Desktop.Views.Controls.DesignMath.Bytes(3.4 * 1024 * 1024 * 1024));
        }
        finally { Loc.Use("pt-BR"); }
    }

    [Theory]
    [InlineData(null, null, "pt-BR", "pt-BR")]
    [InlineData(null, null, "en-US", "en")]
    [InlineData(null, null, "es-MX", "es")]
    [InlineData(null, null, "pt-PT", "pt-BR")]
    [InlineData(null, null, "fr-FR", "pt-BR")]
    [InlineData(null, null, "", "pt-BR")]
    [InlineData(null, "system", "en-GB", "en")]
    [InlineData(null, "es", "en-US", "es")]
    [InlineData(null, "EN", "pt-BR", "en")]
    [InlineData(null, "klingon", "es-ES", "es")]
    [InlineData("en", "es", "pt-BR", "en")]
    [InlineData("es-AR", null, "pt-BR", "es")]
    [InlineData("zz", "en", "pt-BR", "en")]
    public void Language_resolution_order(string? arg, string? setting, string windows, string expected) =>
        Assert.Equal(expected, LanguageOption.Resolve(arg, setting, CultureInfo.GetCultureInfo(windows)));

    [Fact]
    public void Lang_argument_is_read_from_the_command_line()
    {
        Assert.Equal("en", LanguageOption.FromArgs(["--selftest-ui", "r.json", "--lang", "en"]));
        Assert.Equal("es", LanguageOption.FromArgs(["--lang=es"]));
        Assert.Null(LanguageOption.FromArgs(["--lang"]));
        Assert.Null(LanguageOption.FromArgs([]));
        Assert.Equal(LanguageOption.Supported, LanguageOption.Choices.Select(c => c.Code));
    }

    [Fact]
    public void Published_build_ships_every_language()
    {
        var publish = File.ReadAllText(Path.Combine(Root(), "tools", "packaging", "publish.py"));
        var m = SatelliteRegex().Match(publish);
        Assert.True(m.Success, "publish.py: SatelliteResourceLanguages não encontrado");
        var langs = m.Groups[1].Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var l in LanguageOption.Supported) Assert.Contains(l, langs);
        // O instalador copia a pasta inteira (com en\ e es\).
        var iss = File.ReadAllText(Path.Combine(Root(), "tools", "packaging", "nexus.iss"));
        Assert.Contains(@"Source: ""{#SourceDir}\*""; DestDir: ""{app}""; Flags: ignoreversion recursesubdirs", iss, StringComparison.Ordinal);
        var csproj = XDocument.Load(Path.Combine(Root(), "src", "Nexus.Desktop", "Nexus.Desktop.csproj"));
        Assert.Equal("pt-BR", csproj.Descendants("NeutralLanguage").Single().Value);
    }

    [Fact]
    public void Catalog_translations_change_only_texts_and_cover_the_visible_catalogs()
    {
        var dir = Path.Combine(Root(), "catalog");
        var doc = Nexus.Core.CatalogLoader.LoadDirectory(dir);
        var problems = new List<string>();
        foreach (var file in new[] { "actions.v1.json", "tuning.v1.json", "privacy.v1.json", "nvidia.v1.json" })
        {
            var ids = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, file))).RootElement.GetProperty("actions")
                .EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToList();
            foreach (var id in ids)
            {
                var e = doc.Actions.First(a => a.Definition.Id == id);
                foreach (var lang in new[] { "en", "es" })
                    if (!e.Translations.TryGetValue(lang, out var t) || string.IsNullOrWhiteSpace(t.Title)) problems.Add($"{file} {id}: sem título em {lang}");
            }
        }
        foreach (var e in doc.Actions.Where(a => a.Translations.Count > 0))
            foreach (var lang in e.Translations.Keys)
                if (!LanguageOption.Supported.Contains(lang, StringComparer.OrdinalIgnoreCase)) problems.Add($"{e.Definition.Id}: idioma desconhecido '{lang}'");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var original = doc.Actions.First(a => a.Definition.Id == "win.gamemode.enable");
        var en = original.Localized("en-US");
        Assert.Equal("Turn on Game Mode", en.Definition.Title);
        Assert.Equal("Activar el Modo Juego", original.Localized("es").Definition.Title);
        Assert.Same(original, original.Localized("fr"));
        // Só texto muda: chave, handler, alvo, valor e privilégio são os mesmos (o aplicador usa o catálogo original).
        Assert.Equal(original.Definition.Key, en.Definition.Key);
        Assert.Equal(original.Definition.HandlerKind, en.Definition.HandlerKind);
        Assert.Equal(original.Definition.Privileges, en.Definition.Privileges);
        Assert.Equal(original.DesiredValue, en.DesiredValue);
        Assert.Equal(original.TargetTemplate, en.TargetTemplate);
        Assert.Equal(original.Definition.Mechanism, en.Definition.Mechanism); // sem tradução do mecanismo: continua pt-BR
        Assert.Equal(doc.Actions.Count, doc.Localized("en").Actions.Count);
    }

    // ------------------------------------------------------------------ apoio

    private static Dictionary<string, Dictionary<string, string>> AllResources()
    {
        var dir = Path.Combine(Root(), "src", "Nexus.Desktop", "Localization");
        return new()
        {
            ["pt-BR"] = ReadResx(Path.Combine(dir, "Strings.resx")),
            ["en"] = ReadResx(Path.Combine(dir, "Strings.en.resx")),
            ["es"] = ReadResx(Path.Combine(dir, "Strings.es.resx")),
        };
    }

    private static Dictionary<string, string> ReadResx(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in XDocument.Load(path).Root!.Elements("data"))
        {
            var name = (string)data.Attribute("name")!;
            Assert.True(d.TryAdd(name, (string?)data.Element("value") ?? ""), $"{Path.GetFileName(path)}: chave repetida '{name}'");
        }
        return d;
    }

    private static List<int> Placeholders(string s) =>
        PlaceholderRegex().Matches(s.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal))
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToList();

    private static List<(string Key, string Where)> UsedKeys()
    {
        var desktop = Path.Combine(Root(), "src", "Nexus.Desktop");
        var used = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(desktop, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var regex = file.EndsWith(".xaml", StringComparison.Ordinal) ? XamlKeyRegex() : file.EndsWith(".cs", StringComparison.Ordinal) ? CodeKeyRegex() : null;
            if (regex is null) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in regex.Matches(text)) used.Add((m.Groups[1].Value, Path.GetFileName(file)));
        }
        return used;
    }

    private static IEnumerable<string> XamlFiles()
    {
        var desktop = Path.Combine(Root(), "src", "Nexus.Desktop");
        return Directory.EnumerateFiles(Path.Combine(desktop, "Views"), "*.xaml", SearchOption.AllDirectories)
            .Append(Path.Combine(desktop, "MainWindow.xaml"))
            .Concat(Directory.EnumerateFiles(Path.Combine(desktop, "Themes"), "*.xaml"));
    }

    private static IEnumerable<(int Line, string Attribute, string Value)> FixedTexts(string file)
    {
        var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
        foreach (var element in doc.Descendants())
        {
            var setterProperty = element.Name.LocalName == "Setter" ? (string?)element.Attribute("Property") : null;
            foreach (var a in element.Attributes())
            {
                var name = a.Name.LocalName;
                var isText = TextAttributes.Contains(name) || (name == "Value" && setterProperty is not null && TextAttributes.Contains(setterProperty));
                if (!isText || a.IsNamespaceDeclaration || a.Value.StartsWith('{')) continue;
                if (!LettersRegex().IsMatch(a.Value) || XamlLiteralExemptions.Contains(a.Value.Trim())) continue;
                yield return (((System.Xml.IXmlLineInfo)element).LineNumber, name, a.Value);
            }
            foreach (var a in element.Attributes())
            {
                // StringFormat='texto {0}' dentro de um Binding também é texto fixo.
                var sf = StringFormatRegex().Match(a.Value);
                if (sf.Success && LettersRegex().IsMatch(BracesRegex().Replace(sf.Groups[1].Value, "")))
                    yield return (((System.Xml.IXmlLineInfo)element).LineNumber, a.Name.LocalName + ".StringFormat", sf.Groups[1].Value);
            }
            if (!element.HasElements && element.Name.LocalName is "TextBlock" or "Run" or "Hyperlink" or "Label" && LettersRegex().IsMatch(element.Value))
                yield return (((System.Xml.IXmlLineInfo)element).LineNumber, element.Name.LocalName, element.Value.Trim());
        }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nexus.slnx não encontrado");
    }

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:,[^}:]*)?(?::[^}]*)?\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\{l:T\s+(?:Key=)?([\w.]+)\s*\}")]
    private static partial Regex XamlKeyRegex();

    [GeneratedRegex(@"\bLoc\.[TF]\(\s*""([\w.]+)""")]
    private static partial Regex CodeKeyRegex();

    [GeneratedRegex(@"[A-Za-zÀ-ÿ]{2,}")]
    private static partial Regex LettersRegex();

    [GeneratedRegex(@"StringFormat='([^']*)'")]
    private static partial Regex StringFormatRegex();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex BracesRegex();

    [GeneratedRegex(@"SatelliteResourceLanguages=([\w;-]+)")]
    private static partial Regex SatelliteRegex();
}

/// <summary>Coleção que serializa os testes que dependem do idioma global de <see cref="Loc"/>.</summary>
[CollectionDefinition(Name)]
public sealed class LocalizationState
{
    public const string Name = "Idioma da interface (Loc)";
}
