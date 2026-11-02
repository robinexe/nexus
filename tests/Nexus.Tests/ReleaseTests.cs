using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nexus.Tests;

/// <summary>
/// Preparação da versão (docs/08-packaging.md, seção 8): a versão do Directory.Build.props tem seção no CHANGELOG.md com o
/// resumo do aviso de atualização; Termos de uso e Política de privacidade existem, dizem o que precisam dizer e entram no
/// instalador e na pasta publicada.
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class ReleaseTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Wiring.Root, .. parts]));

    private static string VersionPrefix() =>
        XDocument.Parse(Read("Directory.Build.props")).Descendants("VersionPrefix").Single().Value;

    [GeneratedRegex(@"^\d+\.\d+\.\d+\z")]
    private static partial Regex SemVerCore();

    [Fact]
    public void Version_prefix_has_a_changelog_section_with_a_short_update_note()
    {
        var version = VersionPrefix();
        Assert.Matches(SemVerCore(), version);
        var changelog = Read("CHANGELOG.md").Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = changelog.IndexOf($"\n## {version}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"CHANGELOG.md sem a seção \"## {version}\"");
        var section = changelog[(start + 1)..];
        var next = section.IndexOf("\n## ", 3, StringComparison.Ordinal);
        if (next > 0) section = section[..next];
        var note = section.Split('\n').FirstOrDefault(l => l.StartsWith('>'));
        Assert.NotNull(note);
        Assert.InRange(note!.TrimStart('>').Trim().Length, 10, 300);
    }

    [Fact]
    public void Terms_and_privacy_say_what_they_must()
    {
        var terms = Read("docs", "legal", "TERMOS-DE-USO.md");
        var privacy = Read("docs", "legal", "PRIVACIDADE.md");
        foreach (var must in new[] { "sua conta e risco", "Não é afiliado", "Microsoft", "NVIDIA", "AMD", "Intel", "PresentMon", "MIT", "desfazer" })
            Assert.Contains(must, terms, StringComparison.OrdinalIgnoreCase);
        foreach (var must in new[] { "telemetria", "conta", @"%LocalAppData%\NEXUS", @"%ProgramData%\NEXUS", "quarentena", "Windows Update",
                     "github.com/robinexe/nexus-releases", "nvidia.com", "amd.com", "links", "PresentMon" })
            Assert.Contains(must, privacy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installer_shows_the_terms_and_publication_generates_them()
    {
        var iss = Read("tools", "packaging", "nexus.iss");
        Assert.Contains(@"LicenseFile={#SourceDir}\legal\TERMOS-DE-USO.txt", iss, StringComparison.Ordinal);
        Assert.Contains("compiler:Languages\\BrazilianPortuguese.isl", iss, StringComparison.Ordinal);
        var publish = Read("tools", "packaging", "publish.py");
        Assert.Contains("legal_docs.write(target)", publish, StringComparison.Ordinal);
        Assert.Contains("LEGAL_FILES", publish, StringComparison.Ordinal);
        var legal = Read("tools", "packaging", "legal_docs.py");
        Assert.Contains("\"TERMOS-DE-USO\"", legal, StringComparison.Ordinal);
        Assert.Contains("\"PRIVACIDADE\"", legal, StringComparison.Ordinal);
    }
}
