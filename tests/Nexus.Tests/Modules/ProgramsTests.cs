using Nexus.Broker;
using Nexus.Core;
using Nexus.Programs;
using Nexus.Programs.Inventory;
using Nexus.Programs.Leftovers;
using Nexus.Programs.Uninstall;
using Nexus.Programs.Updates;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

// ====================================================================== desinstalador (UninstallString)

[Trait("Category", "Unit")]
public class ProgramsUninstallStringTests
{
    private const string Sys = @"C:\Windows\System32";

    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows\System32\msiexec.exe",
        @"C:\Windows\System32\rundll32.exe",
        @"C:\Program Files\7-Zip\Uninstall.exe",
        @"C:\Program Files (x86)\Steam\uninstall.exe",
        @"C:\Program Files\Some App\bin\unins000.exe",
        @"C:\PROGRA~1\Foo\uninst.exe",
        @"C:\ProgramData\Package Cache\{11111111-2222-3333-4444-555555555555}\setup.exe",
        @"C:\Program.exe", // armadilha clássica do caminho sem aspas
        @"C:\Program Files\Tricky App.exe",
        @"C:\Program Files\Tricky App.exe Folder\real uninstaller.exe",
    };

    private static UninstallCommand? P(string raw, out string? error) =>
        UninstallCommandParser.Parse(raw, Files.Contains, s => s.Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase), Sys, out error);

    [Theory]
    [InlineData("MsiExec.exe /X{A1B2C3D4-1111-2222-3333-444455556666}", "{A1B2C3D4-1111-2222-3333-444455556666}")]
    [InlineData("MsiExec.exe /I{a1b2c3d4-1111-2222-3333-444455556666}", "{A1B2C3D4-1111-2222-3333-444455556666}")] // /I (reparar) vira /x
    [InlineData("msiexec /x {A1B2C3D4-1111-2222-3333-444455556666} /qn", "{A1B2C3D4-1111-2222-3333-444455556666}")]
    [InlineData("\"C:\\Windows\\SysWOW64\\msiexec.exe\" /X{A1B2C3D4-1111-2222-3333-444455556666}", "{A1B2C3D4-1111-2222-3333-444455556666}")]
    public void Msi_vira_msiexec_do_sistema_com_x_e_codigo(string raw, string code)
    {
        var c = P(raw, out var error);
        Assert.Null(error);
        Assert.NotNull(c);
        Assert.True(c.IsMsi);
        Assert.Equal(@"C:\Windows\System32\msiexec.exe", c.FileName, ignoreCase: true);
        Assert.Equal("/x " + code, c.Arguments);
        Assert.Equal(code, c.ProductCode);
    }

    [Fact]
    public void Msi_sem_guid_e_recusado()
    {
        Assert.Null(P("MsiExec.exe /X", out var error));
        Assert.Contains("código de produto", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\7-Zip\\Uninstall.exe\"", @"C:\Program Files\7-Zip\Uninstall.exe", "")]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\uninstall.exe\" /S", @"C:\Program Files (x86)\Steam\uninstall.exe", "/S")]
    [InlineData("C:\\Program Files\\Some App\\bin\\unins000.exe /SILENT /LOG=\"x y.txt\"", @"C:\Program Files\Some App\bin\unins000.exe", "/SILENT /LOG=\"x y.txt\"")]
    [InlineData("C:\\PROGRA~1\\Foo\\uninst.exe", @"C:\PROGRA~1\Foo\uninst.exe", "")]
    [InlineData("%ProgramFiles%\\7-Zip\\Uninstall.exe", @"C:\Program Files\7-Zip\Uninstall.exe", "")]
    [InlineData("  \"C:\\ProgramData\\Package Cache\\{11111111-2222-3333-4444-555555555555}\\setup.exe\" /uninstall /quiet  ",
        @"C:\ProgramData\Package Cache\{11111111-2222-3333-4444-555555555555}\setup.exe", "/uninstall /quiet")]
    [InlineData("rundll32.exe \"C:\\Program Files\\Foo\\x.dll\",Uninstall", @"C:\Windows\System32\rundll32.exe", "\"C:\\Program Files\\Foo\\x.dll\",Uninstall")]
    public void Caminhos_com_e_sem_aspas(string raw, string file, string args)
    {
        var c = P(raw, out var error);
        Assert.Null(error);
        Assert.NotNull(c);
        Assert.False(c.IsMsi);
        Assert.Equal(file, c.FileName);
        Assert.Equal(args, c.Arguments);
    }

    [Fact]
    public void Sem_aspas_nunca_escolhe_prefixo_sem_extensao_e_prefere_o_exe_existente()
    {
        // "C:\Program.exe" existe, mas o prefixo "C:\Program" não termina em .exe: só prefixos terminados em .exe contam.
        var c = P(@"C:\Program Files\Tricky App.exe Folder\real uninstaller.exe /S", out var error);
        Assert.Null(error);
        // "C:\Program Files\Tricky App.exe" existe e termina em .exe: é o primeiro prefixo .exe existente (o Windows faria igual).
        Assert.Equal(@"C:\Program Files\Tricky App.exe", c!.FileName);
    }

    [Theory]
    [InlineData("cmd.exe /c del C:\\x", "interpretador")]
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\" /c \"\"C:\\x\\u.bat\"\"", "interpretador")]
    [InlineData("powershell.exe -File C:\\x\\u.ps1", "interpretador")]
    [InlineData("C:\\Windows\\System32\\wscript.exe C:\\x\\u.vbs", "interpretador")]
    [InlineData("\"C:\\Program Files\\Gone\\uninst.exe\"", "não existe")]
    [InlineData("uninst.exe /S", "caminho completo")]
    [InlineData("\"C:\\Program Files\\Foo\\u.bat\"", ".exe")]
    [InlineData("\"C:\\Program Files\\x.exe", "Aspas")]
    [InlineData("", "não registrou")]
    public void Recusas(string raw, string expected)
    {
        Assert.Null(P(raw, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Quebra_de_linha_e_recusada()
    {
        Assert.Null(P("\"C:\\Program Files\\7-Zip\\Uninstall.exe\"\r\ncalc.exe", out var error));
        Assert.Contains("quebra de linha", error, StringComparison.Ordinal);
    }
}

// ====================================================================== inventário

[Trait("Category", "Unit")]
public class ProgramsInventoryTests
{
    private sealed class FakeSource(params UninstallEntryRaw[] entries) : IUninstallRegistrySource
    {
        public IReadOnlyList<UninstallEntryRaw> ReadAll() => entries;
    }

    private static UninstallEntryRaw E(InstallScope scope, string key, params (string, string)[] values) =>
        new(scope, key, values.ToDictionary(v => v.Item1, v => v.Item2, StringComparer.OrdinalIgnoreCase));

    private static readonly FakeSource Source = new(
        E(InstallScope.Machine64, "7-Zip", ("DisplayName", "7-Zip 24.08 (x64)"), ("Publisher", "Igor Pavlov"), ("DisplayVersion", "24.08"),
            ("EstimatedSize", "5900"), ("InstallDate", "20240911"), ("InstallLocation", "\"C:\\Program Files\\7-Zip\\\""), ("UninstallString", "\"C:\\Program Files\\7-Zip\\Uninstall.exe\"")),
        E(InstallScope.Machine64, "{AAAAAAAA-1111-2222-3333-444444444444}", ("DisplayName", "Visual C++ Runtime"), ("SystemComponent", "1"), ("UninstallString", "MsiExec.exe /X{AAAAAAAA-1111-2222-3333-444444444444}")),
        E(InstallScope.Machine64, "{BBBBBBBB-1111-2222-3333-444444444444}", ("DisplayName", "Office Update KB123"), ("ParentKeyName", "Office16"), ("UninstallString", "x")),
        E(InstallScope.Machine64, "KB5034441", ("DisplayName", "Security Update for Windows"), ("UninstallString", "x")),
        E(InstallScope.Machine64, "Hotfix1", ("DisplayName", "Hotfix for Foo"), ("ReleaseType", "Hotfix"), ("UninstallString", "x")),
        E(InstallScope.Machine64, "NoName", ("UninstallString", "x")),
        E(InstallScope.Machine64, "NoUninstaller", ("DisplayName", "Driver Thing")),
        E(InstallScope.Machine32, "Steam", ("DisplayName", "Steam"), ("Publisher", "Valve Corporation"), ("DisplayVersion", "2.10.91.91"), ("EstimatedSize", "1048576"),
            ("InstallDate", "2023-01-05"), ("UninstallString", "\"C:\\Program Files (x86)\\Steam\\uninstall.exe\"")),
        E(InstallScope.Machine32, "7-Zip-dup", ("DisplayName", "7-Zip 24.08 (x64)"), ("DisplayVersion", "24.08"), ("UninstallString", "x")),
        E(InstallScope.User, "Discord", ("DisplayName", "Discord"), ("Publisher", "Discord Inc."), ("DisplayVersion", "1.0.9170"), ("InstallDate", "garbage"),
            ("QuietUninstallString", "\"C:\\Users\\a\\AppData\\Local\\Discord\\Update.exe\" --uninstall -s"), ("UninstallString", "\"C:\\Users\\a\\AppData\\Local\\Discord\\Update.exe\" --uninstall"),
            ("NoRemove", "0")),
        E(InstallScope.Machine64, "{CCCCCCCC-1111-2222-3333-444444444444}", ("DisplayName", "Some MSI App"), ("WindowsInstaller", "1"), ("EstimatedSize", "0"),
            ("UninstallString", "MsiExec.exe /I{CCCCCCCC-1111-2222-3333-444444444444}")));

    [Fact]
    public void Filtra_componentes_atualizacoes_sem_nome_sem_desinstalador_e_duplicatas()
    {
        var list = ProgramInventory.Build(Source.ReadAll());
        Assert.Equal(["7-Zip 24.08 (x64)", "Discord", "Some MSI App", "Steam"], list.Select(p => p.Name).ToArray());
        var zip = list[0];
        Assert.Equal(InstallScope.Machine64, zip.Scope); // a duplicata de 32 bits sumiu
        Assert.Equal(5900L * 1024, zip.SizeBytes);
        Assert.Equal(new DateOnly(2024, 9, 11), zip.InstallDate);
        Assert.Equal(@"C:\Program Files\7-Zip", zip.InstallLocation); // aspas e barra final removidas
        var discord = list.Single(p => p.Name == "Discord");
        Assert.Null(discord.InstallDate); // data inválida = desconhecida
        Assert.NotNull(discord.QuietUninstallString);
        Assert.Equal(InstallScope.User, discord.Scope);
        Assert.True(list.Single(p => p.Name == "Some MSI App").IsMsi);
        Assert.Null(list.Single(p => p.Name == "Some MSI App").SizeBytes); // EstimatedSize 0 = desconhecido
        Assert.Equal(new DateOnly(2023, 1, 5), list.Single(p => p.Name == "Steam").InstallDate); // com separadores
    }

    [Fact]
    public void Busca_e_ordenacao()
    {
        var list = ProgramInventory.Build(Source.ReadAll());
        Assert.Equal(["Steam"], ProgramInventory.Filter(list, "valve").Select(p => p.Name).ToArray());
        Assert.Equal(["Discord"], ProgramInventory.Filter(list, "disc inc").Select(p => p.Name).ToArray());
        Assert.Equal("Steam", ProgramInventory.Sort(list, ProgramSort.SizeDescending)[0].Name);
        Assert.Equal("7-Zip 24.08 (x64)", ProgramInventory.Sort(list, ProgramSort.DateDescending)[0].Name);
        Assert.Equal("Some MSI App", ProgramInventory.Sort(list, ProgramSort.Publisher)[^1].Name); // sem editor por último
    }

    [Theory]
    [InlineData("20240911", 2024, 9, 11)]
    [InlineData("2024/09/11", 2024, 9, 11)]
    [InlineData("19000101", 0, 0, 0)]
    [InlineData("2024131", 0, 0, 0)]
    [InlineData("20241301", 0, 0, 0)]
    public void Datas(string raw, int y, int m, int d)
    {
        var parsed = ProgramInventory.ParseInstallDate(raw);
        if (y == 0) Assert.Null(parsed);
        else Assert.Equal(new DateOnly(y, m, d), parsed);
    }

    [Fact]
    public void Tamanho_formatado()
    {
        Assert.Equal("—", ProgramInventory.FormatSize(null));
        Assert.Equal("5,8 MB", ProgramInventory.FormatSize(5900L * 1024));
        Assert.Equal("1,0 GB", ProgramInventory.FormatSize(1024L * 1024 * 1024));
    }

    [Fact]
    public void Apps_da_store_json_objeto_ou_lista()
    {
        const string many = """
            [{"Name":"Microsoft.WindowsCalculator","PackageFullName":"Microsoft.WindowsCalculator_11.2409.0.0_x64__8wekyb3d8bbwe","Publisher":"CN=Microsoft Corporation","Version":"11.2409.0.0","InstallLocation":"C:\\Program Files\\WindowsApps\\x","NonRemovable":false},
             {"Name":"SpotifyAB.SpotifyMusic","PackageFullName":"SpotifyAB.SpotifyMusic_1.2.0.0_x64__zpdnekdrzrea0","Publisher":"CN=Spotify","Version":"1.2.0.0","InstallLocation":null,"NonRemovable":false},
             {"Name":"Broken"}]
            """;
        var apps = StoreApps.Parse(many);
        Assert.Equal(2, apps.Count);
        Assert.Equal("SpotifyMusic", apps[0].DisplayName);
        Assert.Single(StoreApps.Parse("""{"Name":"A.B","PackageFullName":"A.B_1_x64__x","NonRemovable":true}"""));
        Assert.True(StoreApps.Parse("""{"Name":"A.B","PackageFullName":"A.B_1_x64__x","NonRemovable":true}""")[0].NonRemovable);
        Assert.Empty(StoreApps.Parse(""));
    }
}

// ====================================================================== sobras: regras e varredura

internal static class Prog
{
    public static InstalledProgram P(string name, string? publisher = null, string? location = null, string? uninstall = null, string? id = null) =>
        new(id ?? "Machine64|" + name, name, publisher, "1.0", null, null, location, uninstall ?? "\"C:\\x\\u.exe\"", null, null, InstallScope.Machine64, name, false, false);
}

public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "nexus-programs-" + Guid.NewGuid().ToString("N"));
    public string ProgramFiles => Path.Combine(Root, "ProgramFiles");
    public string ProgramData => Path.Combine(Root, "ProgramData");
    public string Roaming => Path.Combine(Root, "Users", "ana", "AppData", "Roaming");
    public string Local => Path.Combine(Root, "Users", "ana", "AppData", "Local");
    public string Data => Path.Combine(Root, "NexusData");

    public TempTree()
    {
        foreach (var d in new[] { ProgramFiles, ProgramData, Roaming, Local, Data }) Directory.CreateDirectory(d);
    }

    public LeftoverRoots Roots => new([ProgramFiles, ProgramData], [Roaming, Local]);

    public static string Dir(params string[] parts)
    {
        var p = Path.Combine(parts);
        Directory.CreateDirectory(p);
        return p;
    }

    public static string File(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[Trait("Category", "Unit")]
public class ProgramsLeftoverRulesTests
{
    [Theory]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla", "Mozilla Firefox", true)]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla", "Firefox", true)]
    [InlineData("7-Zip 24.08 (x64)", "Igor Pavlov", "7-Zip", true)]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++ Team", "Notepad++", true)]
    [InlineData("Google Chrome", "Google LLC", "GoogleChrome", true)]
    [InlineData("Google Chrome", "Google LLC", "Google", false)]
    [InlineData("Steam", "Valve Corporation", "SteamLibrary", false)]
    [InlineData("Go", "Google", "Go", false)] // curto demais para casar sozinho
    public void Casamento_de_nome(string name, string publisher, string folder, bool expected) =>
        Assert.Equal(expected, NameMatching.MatchesProgram(folder, NameMatching.ProgramForms(name, publisher)));

    [Theory]
    [InlineData("Valve Corporation", "Valve", true)]
    [InlineData("Discord Inc.", "Discord", true)]
    [InlineData("Électronique S.A.", "Electronique", true)]
    [InlineData("Igor Pavlov", "Igor", false)]
    public void Casamento_de_editor(string publisher, string folder, bool expected) =>
        Assert.Equal(expected, NameMatching.MatchesPublisher(folder, publisher));

    [Fact]
    public void Pastas_protegidas_raiz_profundidade_e_programas_instalados()
    {
        using var t = new TempTree();
        var roots = new[] { t.ProgramFiles, t.Local };
        var installed = new[] { Prog.P("Thunderbird", "Mozilla", Path.Combine(t.ProgramFiles, "Mozilla Thunderbird")) };
        string? Check(string p) => LeftoverPolicy.CheckFolder(p, roots, installed);

        Assert.Null(Check(Path.Combine(t.ProgramFiles, "Foo App")));
        Assert.Null(Check(Path.Combine(t.Local, "Programs", "foo")));
        Assert.NotNull(Check(t.ProgramFiles));                                          // a raiz
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "Microsoft")));               // compartilhada
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "Microsoft Office", "x")));
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "Common Files", "Foo")));
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "WindowsApps")));
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "NVIDIA Corporation", "x")));
        Assert.NotNull(Check(Path.Combine(t.Local, "Programs")));                       // "Programs" sozinho
        Assert.NotNull(Check(Path.Combine(t.Local, "Packages", "x")));
        Assert.NotNull(Check(Path.Combine(t.Local, "Google")));                         // editor com vários produtos: só subpasta
        Assert.Null(Check(Path.Combine(t.Local, "Google", "Earth")));
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "Mozilla")));                 // editor de um programa instalado
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "Mozilla Thunderbird")));     // pasta de programa instalado
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "Mozilla Thunderbird", "x"))); // dentro dela
        Assert.NotNull(Check(Path.Combine(t.Root, "fora")));                            // fora das raízes
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "a", "..", "b")));            // não canônico
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "Foo") + Path.DirectorySeparatorChar));
        Assert.NotNull(Check("relativo" + Path.DirectorySeparatorChar + "x"));
        Assert.NotNull(Check(Path.Combine(t.ProgramFiles, "a", "b", "c", "d", "e")));    // fundo demais
    }

    [Fact]
    public void Contem_pasta_de_programa_instalado_e_recusado()
    {
        using var t = new TempTree();
        var installed = new[] { Prog.P("Other Tool", "Acme", Path.Combine(t.ProgramFiles, "Acme Suite", "Other Tool")) };
        Assert.NotNull(LeftoverPolicy.CheckFolder(Path.Combine(t.ProgramFiles, "Acme Suite"), [t.ProgramFiles], installed));
    }

    [Theory]
    [InlineData("HKCU", @"Software\Foo Corp\Foo App", null)]
    [InlineData("HKLM", @"Software\WOW6432Node\Foo Corp\Foo App", null)]
    [InlineData("HKCU", @"Software\Mozilla\Firefox", null)]
    [InlineData("HKCU", @"Software", "raiz")]
    [InlineData("HKCU", @"Software\Microsoft\Foo", "compartilhada")]
    [InlineData("HKLM", @"Software\Classes\foo", "compartilhada")]
    [InlineData("HKLM", @"Software\Policies\Foo", "compartilhada")]
    [InlineData("HKCU", @"Software\Mozilla", "vários produtos")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\Foo", "Software")]
    [InlineData("HKCU", @"Software\Foo\\Bar", "inválido")]
    [InlineData("HKCU", @"Software\A\B\C\D", "Fundo")]
    [InlineData("HKCR", @"Software\Foo", "Hive")]
    [InlineData("HKCU", @"Software\Valve Corporation\Steam", "instalado")]
    public void Chaves_de_registro(string hive, string path, string? expected)
    {
        var installed = new[] { Prog.P("Steam", "Valve Corporation") };
        var r = LeftoverPolicy.CheckRegistryKey(hive, path, installed);
        if (expected is null) Assert.Null(r);
        else Assert.Contains(expected, r, StringComparison.OrdinalIgnoreCase);
    }
}

[Trait("Category", "Unit")]
public class ProgramsLeftoverScannerTests
{
    [Fact]
    public void Acha_pastas_e_chaves_com_pontuacao_e_respeita_exclusoes()
    {
        using var t = new TempTree();
        var removed = Prog.P("Foo Editor 2.1 (x64)", "Acme Software Inc.", Path.Combine(t.ProgramFiles, "Acme", "Foo Editor"));
        var installed = new[] { Prog.P("Bar Viewer", "Acme Software Inc.", Path.Combine(t.ProgramFiles, "Acme", "Bar Viewer")), removed };

        TempTree.File(Path.Combine(t.ProgramFiles, "Acme", "Foo Editor", "plugins", "a.dll"), "12345");  // InstallLocation que sobrou
        TempTree.File(Path.Combine(t.ProgramFiles, "Acme", "Bar Viewer", "bar.exe"));                     // de outro programa do editor
        TempTree.File(Path.Combine(t.Roaming, "Foo Editor", "settings.json"), "{}");                      // nome do programa
        TempTree.File(Path.Combine(t.Local, "Acme", "Foo Editor", "cache.bin"));                          // editor\programa (editor compartilhado)
        TempTree.File(Path.Combine(t.Local, "Programs", "foo-editor", "x.exe"));                          // instalação por usuário
        TempTree.File(Path.Combine(t.ProgramData, "FooEditorUpdater", "log.txt"));                        // nome parecido (baixa)
        TempTree.Dir(t.ProgramFiles, "Common Files", "Foo Editor");                                              // dentro de pasta protegida: não varre
        TempTree.Dir(t.ProgramFiles, "Microsoft");

        var reg = new InMemoryRegistryTreeBackend();
        reg.SetValue(RegHive.HKCU, @"Software\Acme\Foo Editor", "Theme", "String", "dark");
        reg.SetValue(RegHive.HKCU, @"Software\Acme\Bar Viewer", "x", "DWord", "1");
        reg.SetValue(RegHive.HKLM, @"Software\WOW6432Node\FooEditor", "Path", "String", "C:\\x");
        reg.SetValue(RegHive.HKLM, @"Software\Microsoft\Foo Editor", "x", "String", "y");

        var found = LeftoverScanner.Scan(removed, installed, t.Roots, reg);
        string? Score(string path) => found.FirstOrDefault(f => f.Path == path)?.Confidence;

        Assert.Equal("Alta", Score(Path.Combine(t.ProgramFiles, "Acme", "Foo Editor")));
        Assert.Equal(LeftoverScanner.InstallLocationScore, found.First(f => f.Path == Path.Combine(t.ProgramFiles, "Acme", "Foo Editor")).Score);
        Assert.Equal("Alta", Score(Path.Combine(t.Roaming, "Foo Editor")));
        Assert.Equal("Alta", Score(Path.Combine(t.Local, "Programs", "foo-editor")));
        Assert.Null(Score(Path.Combine(t.Local, "Acme")));                         // editor compartilhado com Bar Viewer: nunca a pasta do editor
        Assert.Equal("Alta", Score(Path.Combine(t.Local, "Acme", "Foo Editor")));  // mas a subpasta deste programa, sim
        Assert.Null(Score(Path.Combine(t.ProgramFiles, "Acme", "Bar Viewer")));
        Assert.Null(Score(Path.Combine(t.ProgramFiles, "Common Files", "Foo Editor")));
        Assert.Null(Score(Path.Combine(t.ProgramFiles, "Microsoft")));
        Assert.Equal("Baixa", Score(Path.Combine(t.ProgramData, "FooEditorUpdater")));
        Assert.False(found.First(f => f.Path == Path.Combine(t.ProgramData, "FooEditorUpdater")).Recommended);

        Assert.Contains(found, f => f.Kind == LeftoverKind.RegistryKey && f.MachineScope && f.Path == @"Software\WOW6432Node\FooEditor");
        Assert.DoesNotContain(found, f => f.Kind == LeftoverKind.RegistryKey && f.Path.Contains("Microsoft", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Kind == LeftoverKind.RegistryKey && !f.MachineScope && f.Path == @"Software\Acme\Foo Editor");
        Assert.DoesNotContain(found, f => f.Kind == LeftoverKind.RegistryKey && (f.Path == @"Software\Acme" || f.Path.EndsWith("Bar Viewer", StringComparison.Ordinal)));
        var size = found.First(f => f.Path == Path.Combine(t.ProgramFiles, "Acme", "Foo Editor"));
        Assert.Equal(5, size.SizeBytes);
        Assert.Equal(1, size.FileCount);
    }

    [Fact]
    public void Editor_exclusivo_e_oferecido_inteiro_e_chave_editor_programa()
    {
        using var t = new TempTree();
        var removed = Prog.P("Zed Paint", "Zedsoft GmbH");
        TempTree.File(Path.Combine(t.Roaming, "Zedsoft", "Zed Paint", "a.ini"));
        var reg = new InMemoryRegistryTreeBackend();
        reg.SetValue(RegHive.HKCU, @"Software\Zedsoft\Zed Paint", "a", "String", "b");

        var found = LeftoverScanner.Scan(removed, [removed], t.Roots, reg);
        var folder = Assert.Single(found, f => f.Kind == LeftoverKind.Folder);
        Assert.Equal(Path.Combine(t.Roaming, "Zedsoft"), folder.Path); // a mãe cobre a filha: só uma entrada
        Assert.Equal(LeftoverScanner.VendorProgramFolderScore, folder.Score);
        var key = Assert.Single(found, f => f.Kind == LeftoverKind.RegistryKey);
        Assert.Equal(@"Software\Zedsoft\Zed Paint", key.Path);
        Assert.Equal(@"HKCU\Software\Zedsoft\Zed Paint", key.DisplayPath);
    }

    [Fact]
    public void Links_nao_sao_seguidos()
    {
        if (OperatingSystem.IsWindows()) return; // criar link simbólico no Windows exige privilégio
        using var t = new TempTree();
        var outside = TempTree.Dir(t.Root, "outside", "Zed Paint");
        TempTree.File(Path.Combine(outside, "precious.txt"));
        Directory.CreateSymbolicLink(Path.Combine(t.Roaming, "Zed Paint"), outside);
        var removed = Prog.P("Zed Paint", "Zedsoft");
        Assert.Empty(LeftoverScanner.Scan(removed, [removed], t.Roots, null));
        Assert.True(LeftoverScanner.IsLinkOrUnderLink(Path.Combine(t.Roaming, "Zed Paint", "precious.txt"), t.Roots.User));
    }
}

// ====================================================================== handler ProgramLeftover

[Trait("Category", "Unit")]
public class ProgramLeftoverHandlerTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class Roots(TempTree t, bool userKnown = true) : ILeftoverRootsProvider
    {
        public IReadOnlyList<string> Machine() => t.Roots.Machine;
        public IReadOnlyList<string>? User(string? userSid) => userKnown ? t.Roots.User : null;
    }

    private static ProgramLeftoverHandler Handler(TempTree t, InMemoryRegistryTreeBackend reg, IReadOnlyList<InstalledProgram>? installed = null,
        bool elevated = true, bool otherAccount = false, Func<string, string?>? trust = null, bool crossVolume = false, bool userKnown = true) =>
        new(reg, new Roots(t, userKnown), _ => installed ?? [], t.Data, elevated, otherAccount, trust, crossVolume);

    private static ActionInstance Action(string kind, string scope, string path, string? sid = null)
    {
        var target = new Dictionary<string, string> { ["kind"] = kind, ["scope"] = scope, ["path"] = path };
        if (sid is not null) target[UserHive.TargetKey] = sid;
        return new ActionInstance(T.Def("programs.leftover.test", ProgramLeftoverHandler.Kind, scope == "HKLM" ? PrivilegeLevel.Admin : PrivilegeLevel.User),
            target, ProgramLeftoverHandler.DesiredState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pasta_vai_para_quarentena_e_volta(bool crossVolume)
    {
        using var t = new TempTree();
        var target = Path.Combine(t.ProgramFiles, "Old Game");
        TempTree.File(Path.Combine(target, "save", "slot1.dat"), "progress");
        TempTree.File(Path.Combine(target, "readme.txt"), "hi");
        TempTree.Dir(target, "empty");
        var h = Handler(t, new InMemoryRegistryTreeBackend(), crossVolume: crossVolume);
        var a = Action("file", "HKLM", target);

        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var before = h.Detect(a);
        Assert.Equal(ProgramLeftoverHandler.Present, before.Value);

        h.Apply(a);
        Assert.False(Directory.Exists(target));
        Assert.True(h.Detect(a).SameValueAs(ProgramLeftoverHandler.DesiredState));
        var manifest = h.Quarantine.ReadManifest(new LeftoverTarget(false, true, target, null))!;
        Assert.Equal("done", manifest.State);
        Assert.Equal(2, manifest.Files!.Count);
        Assert.StartsWith(Path.Combine(t.Data, "quarantine", "programs"), h.Quarantine.FolderOf(new LeftoverTarget(false, true, target, null)), StringComparison.Ordinal);

        Assert.Null(h.CheckRestore(a, before));
        h.Restore(a, before);
        Assert.Equal("progress", File.ReadAllText(Path.Combine(target, "save", "slot1.dat")));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        Assert.True(h.Detect(a).SameValueAs(before));
        Assert.False(Directory.Exists(h.Quarantine.FolderOf(new LeftoverTarget(false, true, target, null)))); // quarentena limpa
    }

    [Fact]
    public void Quarentena_da_maquina_e_protegida_e_a_do_usuario_nao_precisa()
    {
        using var t = new TempTree();
        var hardened = new List<string>();
        var h = new ProgramLeftoverHandler(new InMemoryRegistryTreeBackend(), new Roots(t), _ => [], t.Data, true, false,
            hardenMachineQuarantine: hardened.Add);
        var machine = TempTree.File(Path.Combine(t.ProgramData, "Old Tool", "a.log"));
        var user = TempTree.File(Path.Combine(t.Roaming, "Old Tool", "a.ini"));
        h.Apply(Action("file", "HKLM", Path.GetDirectoryName(machine)!));
        var folder = h.Quarantine.FolderOf(new LeftoverTarget(false, true, Path.GetDirectoryName(machine)!, null));
        Assert.Contains(folder, hardened);
        Assert.Contains(Path.Combine(folder, "manifest.json"), hardened);
        hardened.Clear();
        h.Apply(Action("file", "HKCU", Path.GetDirectoryName(user)!));
        Assert.Empty(hardened);
    }

    [Fact]
    public void Arquivo_unico_e_restauracao_sem_sobrescrever()
    {
        using var t = new TempTree();
        var file = TempTree.File(Path.Combine(t.Roaming, "Zed Paint", "zed.ini"), "old");
        var h = Handler(t, new InMemoryRegistryTreeBackend());
        var a = Action("file", "HKCU", file);
        var before = h.Detect(a);
        h.Apply(a);
        Assert.False(File.Exists(file));
        TempTree.File(file, "new"); // algo novo ocupou o lugar
        Assert.Null(h.CheckRestore(a, before));
        h.Restore(a, before);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal("old", File.ReadAllText(file + " (restaurado pelo NEXUS)"));
    }

    [Fact]
    public void Restauracao_nao_passa_por_pasta_mae_trocada_por_link()
    {
        // Revisão de segurança: a conferência de links só rodava se o alvo existisse. Na restauração ele não existe, então uma
        // pasta-mãe trocada por junção (AppData\Roaming\Zedsoft -> pasta protegida) recebia o conteúdo, gravado pelo aplicador
        // (talvez SYSTEM), fora das raízes.
        if (OperatingSystem.IsWindows()) return;
        using var t = new TempTree();
        var target = Path.Combine(t.Roaming, "Zedsoft", "Zed Paint");
        TempTree.File(Path.Combine(target, "evil.dll"), "payload");
        var h = Handler(t, new InMemoryRegistryTreeBackend());
        var a = Action("file", "HKCU", target);
        var before = h.Detect(a);
        h.Apply(a);
        Assert.False(Directory.Exists(target));

        var protectedDir = TempTree.Dir(t.Root, "protegida");
        Directory.Delete(Path.Combine(t.Roaming, "Zedsoft"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(t.Roaming, "Zedsoft"), protectedDir);

        Assert.NotNull(h.CheckRestore(a, before));
        Assert.Throws<InvalidOperationException>(() => h.Restore(a, before));
        Assert.Empty(Directory.EnumerateFileSystemEntries(protectedDir));
        Assert.True(h.Quarantine.PayloadExists(new LeftoverTarget(false, false, target, null))); // continua guardado
    }

    [Fact]
    public void Aplicacao_reconfere_links_logo_antes_de_mover()
    {
        if (OperatingSystem.IsWindows()) return;
        using var t = new TempTree();
        var target = Path.Combine(t.Roaming, "Zedsoft", "Zed Paint");
        TempTree.File(Path.Combine(target, "a.ini"), "x");
        var outside = TempTree.Dir(t.Root, "fora", "Zed Paint");
        TempTree.File(Path.Combine(outside, "precioso.txt"), "não mexer");
        var swapped = false;
        // Troca a pasta-mãe por link depois do inventário, logo antes do movimento (2ª consulta aos programas instalados).
        var calls = 0;
        var h = new ProgramLeftoverHandler(new InMemoryRegistryTreeBackend(), new Roots(t), _ =>
        {
            if (++calls == 2 && !swapped)
            {
                swapped = true;
                Directory.Delete(Path.Combine(t.Roaming, "Zedsoft"), recursive: true);
                Directory.CreateSymbolicLink(Path.Combine(t.Roaming, "Zedsoft"), Path.GetDirectoryName(outside)!);
            }
            return [];
        }, t.Data, true, false);
        Assert.ThrowsAny<Exception>(() => h.Apply(Action("file", "HKCU", target)));
        Assert.True(swapped);
        Assert.Equal("não mexer", File.ReadAllText(Path.Combine(outside, "precioso.txt")));
    }

    [Fact]
    public void Quarentena_adulterada_e_recusada_na_restauracao()
    {
        using var t = new TempTree();
        var target = Path.Combine(t.ProgramFiles, "Old Game");
        TempTree.File(Path.Combine(target, "game.exe"), "original");
        var h = Handler(t, new InMemoryRegistryTreeBackend());
        var a = Action("file", "HKLM", target);
        var before = h.Detect(a);
        h.Apply(a);
        var payload = h.Quarantine.PayloadPath(new LeftoverTarget(false, true, target, null));

        TempTree.File(Path.Combine(payload, "evil.dll"), "mz"); // arquivo a mais
        Assert.Contains("alterada", h.CheckRestore(a, before), StringComparison.Ordinal);
        File.Delete(Path.Combine(payload, "evil.dll"));
        File.WriteAllText(Path.Combine(payload, "game.exe"), "tampered"); // conteúdo trocado (mesmo nome)
        Assert.Contains("alterada", h.CheckRestore(a, before), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(payload, "game.exe"), "original");
        Assert.Null(h.CheckRestore(a, before));

        var untrusted = Handler(t, new InMemoryRegistryTreeBackend(), trust: _ => "gravável por Usuários");
        Assert.Contains("não confiável", untrusted.CheckRestore(a, before), StringComparison.Ordinal);
    }

    [Fact]
    public void Chave_exportada_com_tipos_removida_e_recriada()
    {
        using var t = new TempTree();
        var reg = new InMemoryRegistryTreeBackend();
        const string path = @"Software\Zedsoft\Zed Paint";
        reg.SetValue(RegHive.HKCU, path, "", "String", "default", Sid);
        reg.SetValue(RegHive.HKCU, path, "Size", "DWord", "4294967295", Sid);
        reg.SetValue(RegHive.HKCU, path, "Big", "QWord", "18446744073709551615", Sid);
        reg.SetValue(RegHive.HKCU, path, "Blob", "Binary", "00ff10", Sid);
        reg.SetValue(RegHive.HKCU, path, "List", "MultiString", "a\u0000b", Sid);
        reg.SetValue(RegHive.HKCU, path, "Exp", "ExpandString", "%TEMP%\\x", Sid);
        reg.SetValue(RegHive.HKCU, path + @"\Recent\Deep", "File1", "String", "c:\\a.png", Sid);
        reg.SetValue(RegHive.HKCU, @"Software\Zedsoft\Other", "keep", "String", "1", Sid);
        var h = Handler(t, reg, otherAccount: true);
        var a = Action("registry", "HKCU", path, Sid);

        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var before = h.Detect(a);
        h.Apply(a);
        Assert.False(reg.KeyExists(RegHive.HKCU, path, Sid));
        Assert.True(reg.KeyExists(RegHive.HKCU, @"Software\Zedsoft\Other", Sid)); // irmã intacta
        Assert.True(h.Detect(a).SameValueAs(ProgramLeftoverHandler.DesiredState));

        Assert.Null(h.CheckRestore(a, before));
        h.Restore(a, before);
        Assert.Equal(("QWord", "18446744073709551615"), reg.GetValue(RegHive.HKCU, path, "Big", Sid));
        Assert.Equal(("MultiString", "a\u0000b"), reg.GetValue(RegHive.HKCU, path, "List", Sid));
        Assert.Equal(("String", "default"), reg.GetValue(RegHive.HKCU, path, "", Sid));
        Assert.Equal(("ExpandString", "%TEMP%\\x"), reg.GetValue(RegHive.HKCU, path, "Exp", Sid));
        Assert.Equal(("String", "c:\\a.png"), reg.GetValue(RegHive.HKCU, path + @"\Recent\Deep", "File1", Sid));
        Assert.True(h.Detect(a).SameValueAs(before));
    }

    [Fact]
    public void Restaurar_ausente_nao_faz_nada()
    {
        using var t = new TempTree();
        var h = Handler(t, new InMemoryRegistryTreeBackend());
        var a = Action("file", "HKCU", Path.Combine(t.Roaming, "Nothing Here"));
        Assert.False(h.Detect(a).Exists);
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(a, T.Ctx).Kind);
        Assert.Null(h.CheckRestore(a, StateSnapshot.Absent()));
        h.Restore(a, StateSnapshot.Absent());
        Assert.False(Directory.Exists(Path.Combine(t.Roaming, "Nothing Here")));
    }

    [Fact]
    public void Revalidacao_no_handler_recusa_alvos_proibidos()
    {
        using var t = new TempTree();
        var reg = new InMemoryRegistryTreeBackend();
        reg.SetValue(RegHive.HKCU, @"Software\Microsoft\Office", "x", "String", "y");
        TempTree.Dir(t.ProgramFiles, "Microsoft");
        TempTree.Dir(t.ProgramFiles, "Still Installed");
        TempTree.Dir(t.Root, "Outside");
        var installed = new[] { Prog.P("Still Installed", "Someone", Path.Combine(t.ProgramFiles, "Still Installed")) };
        var h = Handler(t, reg, installed);

        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action("file", "HKLM", Path.Combine(t.ProgramFiles, "Microsoft")), T.Ctx).Kind);
        // Revisão de segurança: nome curto 8.3 (C:\ProgramData\MICROS~1 = Microsoft) e fluxo alternativo passavam pela lista.
        TempTree.Dir(t.ProgramData, "MICROS~1");
        Assert.Contains("nome curto", h.Assess(Action("file", "HKLM", Path.Combine(t.ProgramData, "MICROS~1")), T.Ctx).Reason, StringComparison.Ordinal);
        Assert.Contains("nome curto", h.Assess(Action("file", "HKCU", Path.Combine(t.Roaming, "Zed", "STILLI~1")), T.Ctx).Reason, StringComparison.Ordinal);
        Assert.Contains("nome curto", h.Assess(Action("file", "HKLM", Path.Combine(t.ProgramData, "Old:fluxo")), T.Ctx).Reason, StringComparison.Ordinal);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action("file", "HKLM", Path.Combine(t.ProgramFiles, "Still Installed")), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action("file", "HKLM", Path.Combine(t.Root, "Outside")), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action("file", "HKLM", t.Data), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action("file", "HKCU", Path.Combine(t.ProgramFiles, "X")), T.Ctx).Kind); // raiz de outro escopo
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action("registry", "HKCU", @"Software\Microsoft\Office"), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action("disk", "HKCU", @"C:\x"), T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(Action("file", "HKLM", Path.Combine(t.ProgramFiles, "Microsoft"))));
        Assert.True(Directory.Exists(Path.Combine(t.ProgramFiles, "Microsoft")));

        // Máquina sem administrador; perfil indeterminado com outra conta; SID em HKLM.
        TempTree.Dir(t.ProgramFiles, "Old");
        Assert.Contains("administrador", Handler(t, reg, elevated: false).Assess(Action("file", "HKLM", Path.Combine(t.ProgramFiles, "Old")), T.Ctx).Reason, StringComparison.Ordinal);
        TempTree.Dir(t.Roaming, "Old");
        Assert.Equal(ApplicabilityKind.Blocked, Handler(t, reg, otherAccount: true).Assess(Action("file", "HKCU", Path.Combine(t.Roaming, "Old")), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, Handler(t, reg, userKnown: false).Assess(Action("file", "HKCU", Path.Combine(t.Roaming, "Old"), Sid), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action("file", "HKLM", Path.Combine(t.ProgramFiles, "Old"), Sid), T.Ctx).Kind);
    }

    [Fact]
    public void Pasta_com_link_dentro_nao_e_movida()
    {
        if (OperatingSystem.IsWindows()) return;
        using var t = new TempTree();
        var outside = TempTree.Dir(t.Root, "outside");
        var target = TempTree.Dir(t.Roaming, "Zed Paint");
        Directory.CreateSymbolicLink(Path.Combine(target, "link"), outside);
        var h = Handler(t, new InMemoryRegistryTreeBackend());
        var a = Action("file", "HKCU", target);
        Assert.Throws<IOException>(() => h.Apply(a));
        Assert.True(Directory.Exists(target));
        Assert.False(Directory.Exists(h.Quarantine.FolderOf(new LeftoverTarget(false, false, target, null)))); // manifesto pendente descartado
    }

    [Fact]
    public void Motor_completo_aplica_verifica_e_reverte_pelo_diario()
    {
        using var t = new TempTree();
        var reg = new InMemoryRegistryTreeBackend();
        reg.SetValue(RegHive.HKCU, @"Software\Zedsoft\Zed Paint", "a", "DWord", "7");
        var folder = TempTree.File(Path.Combine(t.Local, "Zed Paint", "cache.bin"), "cache");
        var h = Handler(t, reg);
        var handlers = new HandlerRegistry().Add(h);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var actions = new[]
        {
            Action("file", "HKCU", Path.GetDirectoryName(folder)!) with { Definition = T.Def("programs.leftover.user.01", ProgramLeftoverHandler.Kind) },
            Action("registry", "HKCU", @"Software\Zedsoft\Zed Paint") with { Definition = T.Def("programs.leftover.user.02", ProgramLeftoverHandler.Kind) },
        };
        var run = exec.BeginRun("teste", "t");
        var results = exec.Execute(run, new DesiredStateResolver(handlers).Resolve(actions, T.Ctx), T.Ctx);
        Assert.All(results, r => Assert.Equal(ExecutionOutcome.Verified, r.Outcome));
        Assert.False(File.Exists(folder));
        Assert.False(reg.KeyExists(RegHive.HKCU, @"Software\Zedsoft\Zed Paint", null));

        var reverted = exec.RevertRun(run);
        Assert.All(reverted, r => Assert.Equal(RevertOutcome.Reverted, r.Outcome));
        Assert.Equal("cache", File.ReadAllText(folder));
        Assert.Equal(("DWord", "7"), reg.GetValue(RegHive.HKCU, @"Software\Zedsoft\Zed Paint", "a"));
    }
}

// ====================================================================== catálogo do módulo

[Trait("Category", "Unit")]
public class ProgramsCatalogTests
{
    private static CatalogDocument Load() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    [Fact]
    public void Catalogo_carrega_ids_unicos_e_handlers_registrados()
    {
        var all = Load();
        var mine = all.Actions.Where(a => a.Definition.Id.StartsWith("programs.", StringComparison.Ordinal)).ToList();
        Assert.Equal(32, mine.Count);
        Assert.Equal(all.Actions.Count, all.Actions.Select(a => a.Definition.Key).Distinct().Count());
        var kinds = ProgramsHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), false, false))
            .Select(h => h.HandlerKind).ToHashSet();
        Assert.All(mine, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        Assert.All(mine.Where(a => a.Definition.Id.Contains(".machine.", StringComparison.Ordinal)), a => Assert.Equal(PrivilegeLevel.Admin, a.Definition.Privileges));
        Assert.All(mine.Where(a => a.Definition.Id.Contains(".user.", StringComparison.Ordinal)), a => Assert.Equal(PrivilegeLevel.User, a.Definition.Privileges));
        Assert.All(mine, a => Assert.False(string.IsNullOrWhiteSpace(a.Definition.Mechanism)));
        Assert.All(mine, a => Assert.NotEmpty(a.Definition.Evidence));
    }

    [Theory]
    [InlineData("file", @"C:\Program Files\Old Game", true)]
    [InlineData("file", @"C:\Users\ana\AppData\Roaming\Zed Paint (x64)\a+b #1.ini", true)]
    [InlineData("registry", @"Software\Zedsoft\Zed Paint", true)]
    [InlineData("registry", @"SOFTWARE\WOW6432Node\Foo\Bar", true)]
    [InlineData("file", @"C:\Program Files\..\Windows", false)]
    [InlineData("file", @"C:\Program Files\x\", false)]
    [InlineData("file", @"\\server\share\x", false)]
    [InlineData("file", @"C:\a\b?", false)]
    [InlineData("file", "C:\\a\r\nb", false)]
    [InlineData("registry", @"System\CurrentControlSet\Services\x", false)]
    [InlineData("registry", @"Software", false)]
    [InlineData("delete", @"C:\x", false)]
    public void Regex_dos_parametros_do_usuario(string kind, string path, bool ok)
    {
        var entry = Load().Actions.Single(a => a.Definition.Id == "programs.leftover.user.01");
        var args = entry.ValidateUserArguments(new Dictionary<string, string> { ["kind"] = kind, ["path"] = path }, out var reason);
        Assert.Equal(ok, args is not null);
        if (ok)
        {
            var instance = entry.TryInstantiate(args!, out reason);
            Assert.NotNull(instance);
            Assert.Equal("HKCU", instance.Target["scope"]);
            Assert.True(instance.Desired.SameValueAs(ProgramLeftoverHandler.DesiredState));
            Assert.True(UserProfileScope.IsUserScoped(instance)); // o broker como serviço acrescenta o SID de quem pede
        }
    }

    [Fact]
    public void Restauracao_pelo_catalogo_reconstroi_e_revalida_o_alvo()
    {
        using var t = new TempTree();
        var catalog = Load();
        var entry = catalog.Actions.Single(a => a.Definition.Id == "programs.leftover.machine.03");
        var instance = entry.TryInstantiate(new Dictionary<string, string> { ["kind"] = "registry", ["path"] = @"Software\Zedsoft\Zed Paint" }, out _)!;
        var inner = new ProgramLeftoverHandler(new InMemoryRegistryTreeBackend(), new FixedRoots(t), _ => [], t.Data, true, false);
        var bound = new CatalogBoundHandler(inner, catalog, () => new Dictionary<string, string>());
        Assert.Null(bound.CheckRestore(instance, StateSnapshot.Absent()));
        var forged = instance with { Target = new Dictionary<string, string>(instance.Target) { ["scope"] = "HKCU" } };
        Assert.NotNull(bound.CheckRestore(forged, StateSnapshot.Absent())); // alvo diferente do que o catálogo produz
    }

    private sealed class FixedRoots(TempTree t) : ILeftoverRootsProvider
    {
        public IReadOnlyList<string> Machine() => t.Roots.Machine;
        public IReadOnlyList<string>? User(string? userSid) => t.Roots.User;
    }
}

// ====================================================================== gerenciadores de pacotes

[Trait("Category", "Unit")]
public class ProgramsPackageParserTests
{
    private const string WingetEnglish =
        "   -\r   \\\r   |\r   /\r                                                                                                                        \r" +
        "  ██████████████████████████████  2.00 MB / 2.00 MB\r                                                    \n" +
        """
        Name                                   Id                                  Version          Available        Source
        -------------------------------------------------------------------------------------------------------------------------
        Mozilla Firefox (x64 en-US)            Mozilla.Firefox                     131.0.3          132.0            winget
        Microsoft Visual C++ 2015-2022 Redist… Microsoft.VCRedist.2015+.x64        14.38.33135.0    14.40.33810.0    winget
        Discord                                Discord.Discord                     Unknown          1.0.9170         winget
        微信 WeChat                            Tencent.WeChat                      3.9.10           3.9.12           winget
        4 upgrades available.

        The following packages have an upgrade available, but require explicit targeting for upgrade:
        Name      Id                Version      Available    Source
        ------------------------------------------------------------------
        Spotify   Spotify.Spotify   1.2.45.454   1.2.48.405   winget
        """;

    private const string WingetPortuguese = """
        Nome                                     ID                              Versão         Disponível     Origem
        -------------------------------------------------------------------------------------------------------------------
        Google Chrome                            Google.Chrome                   129.0.6668.90  130.0.6723.70  winget
        Steam                                    Valve.Steam                     2.10.91.91     3.0            winget
        Ferramenta de Atualização de Código Ab…  SomeVendor.VeryLongPackageIde…  1.0            2.0            winget
        Paint.NET                                9N1F4K2V2HBZ                    5.0.13         5.1            msstore
        4 atualizações disponíveis.
        1 pacote(s) têm números de versão que não podem ser determinados. Use --include-unknown para ver todos os resultados.
        """;

    [Fact]
    public void Winget_ingles_com_progresso_nomes_truncados_largura_asiatica_e_tabela_explicita()
    {
        var list = WingetParser.ParseUpgradeTable(WingetEnglish.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(5, list.Count);
        Assert.Equal(("Mozilla Firefox (x64 en-US)", "Mozilla.Firefox", "131.0.3", "132.0", "winget"),
            (list[0].Name, list[0].Id, list[0].Current, list[0].Available, list[0].Source));
        Assert.Equal("Microsoft Visual C++ 2015-2022 Redist…", list[1].Name);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", list[1].Id);
        Assert.True(list[1].CanUpgrade);
        Assert.Equal("Unknown", list[2].Current);
        Assert.Equal(("微信 WeChat", "Tencent.WeChat", "3.9.10"), (list[3].Name, list[3].Id, list[3].Current));
        Assert.True(list[4].RequiresExplicit);
        Assert.Equal("Spotify.Spotify", list[4].Id);
        Assert.False(list[0].RequiresExplicit);
    }

    [Fact]
    public void Winget_portugues_com_id_truncado_e_store()
    {
        var list = WingetParser.ParseUpgradeTable(WingetPortuguese);
        Assert.Equal(4, list.Count);
        Assert.Equal("130.0.6723.70", list[0].Available);
        var cut = list[2];
        Assert.Equal("Ferramenta de Atualização de Código Ab…", cut.Name);
        Assert.True(cut.IdTruncated);
        Assert.False(cut.CanUpgrade); // ID cortado não pode ser pedido
        Assert.Equal(("9N1F4K2V2HBZ", "msstore"), (list[3].Id, list[3].Source));
        Assert.True(list[3].CanUpgrade);
    }

    [Theory]
    [InlineData("No installed package found matching input criteria.")]
    [InlineData("Nenhuma atualização disponível encontrada.")]
    [InlineData("")]
    public void Winget_sem_atualizacoes(string output) => Assert.Empty(WingetParser.ParseUpgradeTable(output));

    [Fact]
    public void Winget_termos_da_origem() =>
        Assert.True(WingetParser.NeedsSourceAgreement("The `msstore` source requires that you view the following agreements before using."));

    [Fact]
    public void Winget_json_do_modulo_oficial()
    {
        var list = WingetParser.ParseClientJson("""
            [{"Name":"Git","Id":"Git.Git","InstalledVersion":"2.46.0","AvailableVersion":"2.47.0","Source":"winget"},
             {"Name":"Broken","Id":"X.Y"}]
            """);
        var only = Assert.Single(list);
        Assert.Equal(("Git.Git", "2.46.0", "2.47.0"), (only.Id, only.Current, only.Available));
        Assert.Single(WingetParser.ParseClientJson("""{"Name":"Git","Id":"Git.Git","InstalledVersion":"2.46.0","AvailableVersion":"2.47.0","Source":"winget"}"""));
    }

    [Fact]
    public void Chocolatey_outdated_r()
    {
        const string output = """
            Chocolatey v2.3.0
            git|2.46.0|2.47.0|false
            googlechrome|129.0.6668.90|130.0.6723.70|true
            vlc|3.0.20|3.0.21|false
            broken line
            a|b|c
            """;
        var list = ChocolateyParser.ParseOutdated(output);
        Assert.Equal(["git", "googlechrome", "vlc"], list.Select(u => u.Id).ToArray());
        Assert.True(list[1].Pinned);
        Assert.False(list[1].CanUpgrade);
        Assert.Equal(("2.46.0", "2.47.0"), (list[0].Current, list[0].Available));
    }

    [Fact]
    public void Scoop_tabela_nova()
    {
        const string output = """
            WARN  Scoop out of date. Run 'scoop update' to get the latest changes.

            Name      Installed Version Latest Version   Missing Dependencies Info
            ----      ----------------- --------------   -------------------- ----
            7zip      23.01             24.08
            git       2.46.0.windows.1  2.47.0.windows.2
            nodejs    20.1.0                                                  Install failed
            python    3.12.6            3.13.0                                Held package

            """;
        var list = ScoopParser.ParseStatus(output);
        Assert.Equal(["7zip", "git", "python"], list.Select(u => u.Id).ToArray());
        Assert.Equal("2.47.0.windows.2", list[1].Available);
        Assert.True(list[2].Pinned);
        Assert.True(ScoopParser.ScoopOutOfDate(output));
    }

    [Fact]
    public void Scoop_formato_antigo()
    {
        const string output = """
            Scoop is up to date.
            Updates are available for:
                git: 2.26.0.windows.1 -> 2.27.0.windows.1
                extras/vscode: 1.45.1 -> 1.46.0
            These apps failed to install:
                nodejs
            """;
        var list = ScoopParser.ParseStatus(output);
        Assert.Equal(["git", "extras/vscode"], list.Select(u => u.Id).ToArray());
        Assert.True(list[1].CanUpgrade);
        Assert.Empty(ScoopParser.ParseStatus("Scoop is up to date.\nEverything is ok!"));
    }

    [Fact]
    public void Npm_outdated_json()
    {
        const string output = """
            {
              "npm": { "current": "10.2.4", "wanted": "10.9.0", "latest": "10.9.0", "dependent": "global", "location": "C:\\Users\\a\\AppData\\Roaming\\npm\\node_modules\\npm" },
              "@angular/cli": { "current": "17.0.0", "wanted": "17.3.10", "latest": "18.2.9", "dependent": "global", "location": "x" },
              "typescript": { "current": "5.6.3", "wanted": "5.6.3", "latest": "5.6.3" },
              "missing": { "wanted": "1.0.0", "latest": "1.0.0" }
            }
            """;
        var list = NpmParser.ParseOutdated(output);
        Assert.Equal(["@angular/cli", "npm"], list.Select(u => u.Id).ToArray());
        Assert.Equal("18.2.9", list[0].Available);
        Assert.All(list, u => Assert.True(u.CanUpgrade));
        Assert.Empty(NpmParser.ParseOutdated(""));
        Assert.Empty(NpmParser.ParseOutdated("{}"));
    }

    [Theory]
    [InlineData(PackageManagerKind.Winget, "Microsoft.VCRedist.2015+.x64", true)]
    [InlineData(PackageManagerKind.Winget, "9N1F4K2V2HBZ", true)]
    [InlineData(PackageManagerKind.Winget, "Foo'; Remove-Item C:\\ -Recurse; '", false)]
    [InlineData(PackageManagerKind.Winget, "ARP\\Machine\\X64\\Foo", false)]
    [InlineData(PackageManagerKind.Chocolatey, "googlechrome", true)]
    [InlineData(PackageManagerKind.Chocolatey, "a b", false)]
    [InlineData(PackageManagerKind.Scoop, "extras/vscode", true)]
    [InlineData(PackageManagerKind.Scoop, "../x", false)]
    [InlineData(PackageManagerKind.Npm, "@angular/cli", true)]
    [InlineData(PackageManagerKind.Npm, "Typescript", false)]
    [InlineData(PackageManagerKind.Npm, "x;calc", false)]
    public void Ids_validos(PackageManagerKind kind, string id, bool ok) => Assert.Equal(ok, PackageIdRules.IsValid(kind, id));

    [Fact]
    public void Script_de_atualizacao_so_com_ids_validados()
    {
        var items = new[]
        {
            new PackageUpdate(PackageManagerKind.Winget, "Firefox", "Mozilla.Firefox", "1", "2", "winget"),
            new PackageUpdate(PackageManagerKind.Winget, "Paint.NET", "9N1F4K2V2HBZ", "1", "2", "msstore"),
        };
        var script = PackageManagerInfo.BuildUpgradeScript(PackageManagerKind.Winget, @"C:\Users\a\AppData\Local\Microsoft\WindowsApps\winget.exe", items);
        Assert.Contains("upgrade --id 'Mozilla.Firefox' --exact --source 'winget'", script, StringComparison.Ordinal);
        Assert.Contains("upgrade --id '9N1F4K2V2HBZ' --exact --source 'msstore'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--all", script, StringComparison.Ordinal);

        var npm = PackageManagerInfo.BuildUpgradeScript(PackageManagerKind.Npm, @"C:\Program Files\nodejs\npm.cmd",
            [new PackageUpdate(PackageManagerKind.Npm, "@angular/cli", "@angular/cli", "17", "18", "npm")]);
        Assert.Contains("install -g '@angular/cli@latest'", npm, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => PackageManagerInfo.BuildUpgradeScript(PackageManagerKind.Winget, @"C:\w.exe",
            [new PackageUpdate(PackageManagerKind.Winget, "x", "x'; calc; '", "1", "2", "winget")]));
        Assert.Throws<ArgumentException>(() => PackageManagerInfo.BuildUpgradeScript(PackageManagerKind.Winget, @"C:\w.exe",
            [new PackageUpdate(PackageManagerKind.Winget, "x", "A.B", "1", "2", null)])); // sem origem
        Assert.Throws<ArgumentException>(() => PackageManagerInfo.BuildUpgradeScript(PackageManagerKind.Chocolatey, @"C:\it's\choco.exe",
            [new PackageUpdate(PackageManagerKind.Chocolatey, "git", "git", "1", "2")]));
        Assert.Throws<ArgumentException>(() => PackageManagerInfo.BuildUpgradeScript(PackageManagerKind.Scoop, @"C:\s.ps1",
            [new PackageUpdate(PackageManagerKind.Npm, "x", "x", "1", "2")]));
    }
}
