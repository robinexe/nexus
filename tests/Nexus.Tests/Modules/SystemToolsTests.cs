using Nexus.Core;
using Nexus.SystemTools;
using Nexus.SystemTools.Appx;
using Nexus.SystemTools.Drivers;
using Nexus.SystemTools.Network;
using Nexus.SystemTools.Restore;
using Nexus.SystemTools.Wifi;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

// ====================================================================== parsers

[Trait("Category", "Unit")]
public sealed class SystemToolsParserTests
{
    private const string NetshEn = """

        Profiles on interface Wi-Fi:

        Group policy profiles (read only)
        ---------------------------------
            CorpNet

        User profiles
        -------------
            All User Profile     : Casa 5G
            All User Profile     : Café: centro
            Current User Profile : Só meu

        """;

    private const string NetshPt = """

        Perfis na interface Wi-Fi 2:

        Perfis de política de grupo (somente leitura)
        ---------------------------------------------
            <Nenhum>

        Perfis de usuário
        -----------------
            Todos os Perfis de Usuários: Casa 5G
            Perfil de Usuário Atual: Vizinho

        """;

    [Fact]
    public void Netsh_profiles_english()
    {
        var l = WifiParsers.ParseNetshProfiles(NetshEn);
        Assert.Equal(4, l.Profiles.Count);
        Assert.Contains(l.Profiles, p => p.Name == "CorpNet" && p.Scope == WifiProfileScope.GroupPolicy && !p.Removable);
        Assert.Contains(l.Profiles, p => p.Name == "Casa 5G" && p.Scope == WifiProfileScope.AllUsers && p.Interface == "Wi-Fi");
        Assert.Contains(l.Profiles, p => p.Name == "Café: centro"); // dois-pontos no nome
        Assert.Contains(l.Profiles, p => p.Name == "Só meu" && p.Scope == WifiProfileScope.CurrentUser);
    }

    [Fact]
    public void Netsh_profiles_portuguese()
    {
        var l = WifiParsers.ParseNetshProfiles(NetshPt);
        Assert.Equal(2, l.Profiles.Count);
        Assert.All(l.Profiles, p => Assert.Equal("Wi-Fi 2", p.Interface));
        Assert.Contains(l.Profiles, p => p.Name == "Casa 5G" && p.Scope == WifiProfileScope.AllUsers);
        Assert.Contains(l.Profiles, p => p.Name == "Vizinho" && p.Scope == WifiProfileScope.CurrentUser);
    }

    [Fact]
    public void Netsh_windows11_sem_recuo_em_utf8_como_no_pc_real()
    {
        // Saída real (pt-BR, build 26200): linhas de perfil sem recuo e texto em UTF-8 com o console em 850.
        var raw = "\r\nPerfis na interface Wi-Fi:\r\n\r\nPerfis da política de grupo (somente leitura)\r\n---------------------------------\r\n    <Nenhum>\r\n\r\n" +
                  "Perfis do usuário\r\n-------------\r\nTodos os Perfis de Usuários: Angelburger_5G\r\nTodos os Perfis de Usuários: THE FERIAS \r\nTodos os Perfis de Usuários: Gleyckelly❤️\r\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        Assert.True(ProcessText.IsValidUtf8(bytes));
        var l = WifiParsers.ParseNetshProfiles(System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(3, l.Profiles.Count);
        Assert.All(l.Profiles, p => Assert.Equal(WifiProfileScope.AllUsers, p.Scope));
        Assert.Contains(l.Profiles, p => p.Name == "Gleyckelly❤️");
        Assert.All(l.Profiles, p => Assert.Equal("Wi-Fi", p.Interface));
    }

    [Fact]
    public void Codificacao_ascii_e_oem_nao_sao_tratadas_como_utf8()
    {
        Assert.False(ProcessText.IsValidUtf8("pnputil ok"u8));
        Assert.False(ProcessText.IsValidUtf8(new byte[] { 0x50, 0xA0, 0x67 })); // "Pág" em 850
    }

    [Fact]
    public void Netsh_without_wifi_gives_note()
    {
        var l = WifiParsers.ParseNetshProfiles("The Wireless AutoConfig Service (wlansvc) is not running.\r\n");
        Assert.Empty(l.Profiles);
        Assert.NotNull(l.Note);
    }

    [Fact]
    public void Systemtime_bytes()
    {
        byte[] b = [0xEA, 0x07, 10, 0, 5, 0, 31, 0, 14, 0, 30, 0, 5, 0, 0, 0];
        Assert.Equal(new DateTime(2026, 10, 31, 14, 30, 5), WifiParsers.SystemTime(b));
        Assert.Null(WifiParsers.SystemTime([1, 2]));
    }

    private const string PnpEn = """
        Microsoft PnP Utility

        Published Name:     oem3.inf
        Original Name:      nv_dispi.inf
        Provider Name:      NVIDIA
        Class Name:         Display adapters
        Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}
        Driver Version:     07/30/2024 32.0.15.6094
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem7.inf
        Original Name:      nv_dispi.inf
        Provider Name:      NVIDIA
        Class Name:         Display adapters
        Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}
        Extension ID:       {11111111-2222-3333-4444-555555555555}
        Driver Version:     10/01/2025 32.0.15.8129
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem12.inf
        Original Name:      iastorvd.inf
        Provider Name:      Intel Corporation
        Class Name:         Storage controllers
        Class GUID:         {4d36e97b-e325-11ce-bfc1-08002be10318}
        Driver Version:     07/30/2021 18.6.1.1016
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem20.inf
        Original Name:      prnhp001.inf
        Provider Name:      HP
        Class Name:         Printers
        Class GUID:         {4d36e979-e325-11ce-bfc1-08002be10318}
        Driver Version:     06/21/2006 10.0.19041.1
        Signer Name:        Microsoft Windows
        """;

    private const string PnpPt = """
        Utilitário PnP da Microsoft

        Nome Publicado:          oem3.inf
        Nome Original:           nv_dispi.inf
        Nome do Provedor:        NVIDIA
        Nome da Classe:          Adaptadores de vídeo
        GUID da Classe:          {4d36e968-e325-11ce-bfc1-08002be10318}
        Versão do Driver:        30/07/2024 32.0.15.6094
        Nome do Signatário:      Microsoft Windows Hardware Compatibility Publisher

        Nome Publicado:          oem20.inf
        Nome Original:           prnhp001.inf
        Nome do Provedor:        HP
        Nome da Classe:          Impressoras
        GUID da Classe:          {4d36e979-e325-11ce-bfc1-08002be10318}
        Versão do Driver:        05/03/2006 10.0.19041.1
        Nome do Signatário:      Microsoft Windows
        """;

    [Fact]
    public void Pnputil_enum_drivers_english()
    {
        var list = DriverParsers.ParseEnumDrivers(PnpEn);
        Assert.Equal(4, list.Count);
        var nv = list.Single(p => p.PublishedName == "oem3.inf");
        Assert.Equal("nv_dispi.inf", nv.OriginalName);
        Assert.Equal("NVIDIA", nv.Provider);
        Assert.Equal("Display adapters", nv.ClassName);
        Assert.Equal(DriverRules.DisplayClass, nv.ClassGuid);
        Assert.Equal(new Version(32, 0, 15, 6094), nv.Version);
        Assert.Equal(new DateOnly(2024, 7, 30), nv.Date);
        Assert.Equal(DriverRules.DisplayClass, list.Single(p => p.PublishedName == "oem7.inf").ClassGuid); // Extension ID não confunde
        Assert.Equal(new DateOnly(2025, 10, 1), list.Single(p => p.PublishedName == "oem7.inf").Date); // mês/dia (inglês)
    }

    [Fact]
    public void Pnputil_enum_drivers_portuguese()
    {
        var list = DriverParsers.ParseEnumDrivers(PnpPt);
        Assert.Equal(2, list.Count);
        var hp = list.Single(p => p.PublishedName == "oem20.inf");
        Assert.Equal("HP", hp.Provider);
        Assert.Equal("Impressoras", hp.ClassName);
        Assert.Equal(new DateOnly(2006, 3, 5), hp.Date); // dia/mês (português)
        Assert.Equal("Microsoft Windows", hp.Signer);
        Assert.Equal(new DateOnly(2024, 7, 30), list[0].Date);
    }

    private const string DevicesEn = """
        Microsoft PnP Utility

        Instance ID:                PCI\VEN_10DE&DEV_2684\4&1
        Device Description:         NVIDIA GeForce RTX 4090
        Class Name:                 Display
        Class GUID:                 {4d36e968-e325-11ce-bfc1-08002be10318}
        Manufacturer Name:          NVIDIA
        Status:                     Started
        Driver Name:                oem7.inf

        Instance ID:                PCI\VEN_8086&DEV_A0D3\3&2
        Device Description:         Intel RST VMD Controller
        Driver Name:                oem12.inf
        """;

    private const string DevicesPt = """
        Utilitário PnP da Microsoft

        ID da Instância:            PCI\VEN_10DE&DEV_2684\4&1
        Descrição do Dispositivo:   NVIDIA GeForce RTX 4090
        Status:                     Iniciado
        Nome do Driver:             oem7.inf
        """;

    [Fact]
    public void Pnputil_devices_in_use_en_pt()
    {
        var en = DriverParsers.ParseDevicesInUse(DevicesEn, 0)!;
        Assert.Equal(["NVIDIA GeForce RTX 4090"], en["oem7.inf"]);
        Assert.True(en.ContainsKey("OEM12.INF"));
        var pt = DriverParsers.ParseDevicesInUse(DevicesPt, 0)!;
        Assert.Equal(["NVIDIA GeForce RTX 4090"], pt["oem7.inf"]);
        // pnputil antigo (sem /enum-devices) ou saída irreconhecível: "não sei" (nunca "nada em uso").
        Assert.Null(DriverParsers.ParseDevicesInUse("Invalid command", 87));
        Assert.Null(DriverParsers.ParseDevicesInUse("Microsoft PnP Utility\r\n", 0));
    }

    [Fact]
    public void Dns_and_arp_counters()
    {
        const string dns = """
            Windows IP Configuration

                www.example.com
                ----------------------------------------
                Record Name . . . . . : www.example.com
                Record Type . . . . . : 1

                localhost
                ----------------------------------------
                Nome do Registro. . . : localhost
            """;
        Assert.Equal(2, NetworkParsers.CountDnsEntries(dns));
        const string arp = """
            Interface: 192.168.0.10 --- 0x7
              Internet Address      Physical Address      Type
              192.168.0.1           a0-b1-c2-d3-e4-f5     dynamic
              192.168.0.255         ff-ff-ff-ff-ff-ff     static
              224.0.0.22            01-00-5e-00-00-16     static

            Interface: 10.0.0.2 --- 0x9
              Endereço IP           Endereço físico       Tipo
              10.0.0.1              00-11-22-33-44-55     dinâmico
            """;
        Assert.Equal(2, NetworkParsers.CountArpDynamic(arp));
    }

    [Fact]
    public void Appx_json_array_single_and_enum_numbers()
    {
        const string json = """
            [{"Name":"Microsoft.BingNews","PackageFullName":"Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.BingNews_8wekyb3d8bbwe",
              "Publisher":"CN=Microsoft Corporation","Version":"4.55.62231.0","InstallLocation":"C:\\Program Files\\WindowsApps\\Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe",
              "IsFramework":false,"NonRemovable":false,"SignatureKind":"Store"},
             {"Name":"Microsoft.VCLibs.140.00","PackageFullName":"x","PackageFamilyName":"y","Version":{"Major":14,"Minor":0,"Build":33519,"Revision":0},"IsFramework":true,"SignatureKind":3}]
            """;
        var list = AppxParsers.Parse(json);
        Assert.Equal(2, list.Count);
        Assert.Equal("Store", list[0].SignatureKind);
        Assert.False(list[0].IsFramework);
        Assert.Equal("14.0.33519.0", list[1].Version);
        Assert.Equal("Store", list[1].SignatureKind);
        Assert.True(list[1].IsFramework);
        Assert.Single(AppxParsers.Parse("""{"Name":"king.com.CandyCrushSaga","SignatureKind":"Store"}"""));
        Assert.Empty(AppxParsers.Parse(""));
        Assert.Empty(AppxParsers.Parse("[]"));
    }

    [Fact]
    public void Restore_points_json_and_dates()
    {
        const string json = """[{"SequenceNumber":12,"Description":"NEXUS: ponto manual","CreationTime":"20261031143005.000000-180","RestorePointType":12},{"SequenceNumber":11,"Description":"Windows Update","CreationTime":"20261030100000.000000-180","RestorePointType":0}]""";
        var l = RestoreParsers.ParseList(json);
        Assert.Null(l.Error);
        Assert.Equal(2, l.Items!.Count);
        Assert.Equal(12, l.Items[0].Sequence); // mais novo primeiro
        var expected = new DateTimeOffset(2026, 10, 31, 14, 30, 5, TimeSpan.FromHours(-3)).LocalDateTime;
        Assert.Equal(expected, l.Items[0].Created);
        Assert.Equal("Acesso negado", RestoreParsers.ParseList("""{"error":"Acesso negado"}""").Error);
        Assert.Single(RestoreParsers.ParseList("""{"SequenceNumber":1,"Description":"x","CreationTime":"/Date(1700000000000)/","RestorePointType":12}""").Items!);
        Assert.Equal(['C', 'D'], RestoreParsers.ProtectedDrives([@"\\?\Volume{a}\:(C%3A)", @"\\?\Volume{b}\:(d%3A)"]));
        Assert.Empty(RestoreParsers.ProtectedDrives(null));
    }
}

// ====================================================================== regras de proteção

[Trait("Category", "Unit")]
public sealed class SystemToolsRuleTests
{
    private static DriverPackage Pkg(string pub, string orig, string cls, string ver, string provider = "ACME") =>
        new(pub, orig, provider, "x", cls, ver, Version.Parse(ver), null, "s");

    private const string Printer = "{4d36e979-e325-11ce-bfc1-08002be10318}";
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoneInUse = new Dictionary<string, IReadOnlyList<string>>();

    [Fact]
    public void Driver_in_use_or_unknown_is_never_removed()
    {
        var p = Pkg("oem20.inf", "prn.inf", Printer, "1.0.0.0");
        var inUse = new Dictionary<string, IReadOnlyList<string>> { ["oem20.inf"] = ["Impressora HP"] };
        Assert.Contains("Em uso", DriverRules.CheckRemoval(p, [p], inUse));
        Assert.Contains("não foi possível confirmar", DriverRules.CheckRemoval(p, [p], null), StringComparison.OrdinalIgnoreCase);
        Assert.Null(DriverRules.CheckRemoval(p, [p], NoneInUse));
    }

    [Fact]
    public void Critical_classes_are_protected()
    {
        var disk = Pkg("oem12.inf", "iastorvd.inf", "{4d36e97b-e325-11ce-bfc1-08002be10318}", "1.0.0.0");
        Assert.Contains("crítica", DriverRules.CheckRemoval(disk, [disk], NoneInUse));
        var unknown = disk with { ClassGuid = null };
        Assert.NotNull(DriverRules.CheckRemoval(unknown, [unknown], NoneInUse));
    }

    [Fact]
    public void Display_and_net_only_old_duplicates()
    {
        var old = Pkg("oem3.inf", "nv_dispi.inf", DriverRules.DisplayClass, "32.0.15.6094", "NVIDIA");
        var cur = Pkg("oem7.inf", "nv_dispi.inf", DriverRules.DisplayClass, "32.0.15.8129", "NVIDIA");
        var inUse = new Dictionary<string, IReadOnlyList<string>> { ["oem7.inf"] = ["RTX"] };
        Assert.Null(DriverRules.CheckRemoval(old, [old, cur], inUse));
        Assert.Equal("oem7.inf", DriverRules.NewerDuplicate(old, [old, cur])!.PublishedName);
        Assert.NotNull(DriverRules.CheckRemoval(old, [old], NoneInUse)); // única versão de vídeo
        Assert.NotNull(DriverRules.CheckRemoval(cur, [old, cur], inUse)); // a atual está em uso
        Assert.Null(DriverRules.NewerDuplicate(cur, [old, cur]));
        var otherVendor = old with { Provider = "Outro" };
        Assert.Null(DriverRules.NewerDuplicate(otherVendor, [otherVendor, cur]));
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore", AppxClass.Protected)]
    [InlineData("Microsoft.DesktopAppInstaller", AppxClass.Protected)]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop", AppxClass.Protected)]
    [InlineData("Microsoft.NET.Native.Runtime.2.2", AppxClass.Protected)]
    [InlineData("Microsoft.UI.Xaml.2.8", AppxClass.Protected)]
    [InlineData("Microsoft.SecHealthUI", AppxClass.Protected)]
    [InlineData("Microsoft.Xbox.TCUI", AppxClass.Protected)]
    [InlineData("Microsoft.HEVCVideoExtension", AppxClass.Protected)]
    [InlineData("king.com.CandyCrushSaga", AppxClass.Safe)]
    [InlineData("BytedancePte.Ltd.TikTok", AppxClass.Safe)]
    [InlineData("Clipchamp.Clipchamp", AppxClass.Safe)]
    [InlineData("Microsoft.BingNews", AppxClass.Safe)]
    [InlineData("Microsoft.MicrosoftSolitaireCollection", AppxClass.Safe)]
    [InlineData("Microsoft.GetHelp", AppxClass.Safe)]
    [InlineData("Microsoft.WindowsFeedbackHub", AppxClass.Safe)]
    [InlineData("Microsoft.GamingApp", AppxClass.Warn)]
    [InlineData("Microsoft.Windows.Photos", AppxClass.Warn)]
    [InlineData("Microsoft.WindowsCalculator", AppxClass.Warn)]
    [InlineData("Contoso.Desconhecido", AppxClass.Unknown)]
    public void Appx_classification(string name, AppxClass expected) => Assert.Equal(expected, AppxCatalog.Classify(name).Class);

    [Fact]
    public void Appx_removal_rules()
    {
        AppxPackageInfo P(string name, bool fw = false, bool nonRemovable = false, string sig = "Store") =>
            new(name, name + "_1.0.0.0_x64__8wekyb3d8bbwe", name + "_8wekyb3d8bbwe", "CN=x", "1.0.0.0", null, fw, nonRemovable, sig);
        Assert.Null(AppxCatalog.CheckRemoval(P("Microsoft.BingNews")));
        Assert.NotNull(AppxCatalog.CheckRemoval(P("Microsoft.WindowsStore")));
        Assert.NotNull(AppxCatalog.CheckRemoval(P("Contoso.App")));
        Assert.NotNull(AppxCatalog.CheckRemoval(P("Microsoft.BingNews", fw: true)));
        Assert.NotNull(AppxCatalog.CheckRemoval(P("Microsoft.BingNews", nonRemovable: true)));
        Assert.NotNull(AppxCatalog.CheckRemoval(P("Microsoft.BingNews", sig: "System")));
        Assert.True(AppxCatalog.IsValidFullName("Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe"));
        Assert.False(AppxCatalog.IsValidFullName("x'; Remove-Item C:\\ -Recurse #"));
        Assert.False(AppxCatalog.IsValidName("Microsoft.BingNews\n"));
        Assert.False(AppxCatalog.IsValidName("Micro*"));
        Assert.True(AppxPackageHandler.IsWindowsAppsPath(@"C:\Program Files\WindowsApps\Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe"));
        Assert.False(AppxPackageHandler.IsWindowsAppsPath(@"C:\Users\x\Downloads\evil"));
        Assert.False(AppxPackageHandler.IsWindowsAppsPath(@"C:\Program Files\WindowsApps\..\..\evil"));
    }

    [Fact]
    public void Wifi_name_validation()
    {
        Assert.True(WifiProfileHandler.IsValidName("Café: centro 5G"));
        Assert.False(WifiProfileHandler.IsValidName("rede\n"));
        Assert.False(WifiProfileHandler.IsValidName(" com espaço "));
        Assert.False(WifiProfileHandler.IsValidName(new string('a', 300)));
        Assert.True(DriverParsers.IsOemInf("oem12.inf"));
        Assert.False(DriverParsers.IsOemInf("oem12.inf\n"));
        Assert.False(DriverParsers.IsOemInf(@"..\oem12.inf"));
    }
}

// ====================================================================== handlers com backends falsos + catálogo

internal sealed class FakeNetwork : INetworkBackend
{
    public int Dns, Arp;
    public bool Fail;
    public void FlushDns() { if (Fail) throw new InvalidOperationException("falhou"); Dns++; }
    public void FlushArp() => Arp++;
}

internal sealed class FakeWifi : IWifiBackend
{
    public readonly Guid If1 = Guid.NewGuid();
    public readonly Dictionary<(Guid, string), (string Xml, bool User, bool Gp)> Profiles = [];
    public bool Unavailable;
    public IReadOnlyList<WifiProfileRef> Find(string profileName)
    {
        if (Unavailable) throw new WifiUnavailableException("sem wifi");
        return Profiles.Where(kv => kv.Key.Item2 == profileName).Select(kv => new WifiProfileRef(kv.Key.Item1, profileName, kv.Value.User, kv.Value.Gp)).ToList();
    }
    public IReadOnlyList<Guid> Interfaces() => Unavailable ? [] : [If1];
    public string ExportXml(Guid iface, string profileName) => Profiles[(iface, profileName)].Xml;
    public void Delete(Guid iface, string profileName) => Profiles.Remove((iface, profileName));
    public void Import(Guid iface, string xml, bool userOnly)
    {
        var name = System.Text.RegularExpressions.Regex.Match(xml, "<name>(.*?)</name>").Groups[1].Value;
        Profiles[(iface, name)] = (xml, userOnly, false);
    }
}

internal sealed class FakeProtector : IBackupProtector
{
    public string Scope => "teste";
    public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)1).ToArray();
    public byte[] Unprotect(byte[] data) => data.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray();
}

internal sealed class FakeDriverStore : IDriverStore
{
    public readonly List<DriverPackage> Packages = [];
    public Dictionary<string, IReadOnlyList<string>>? Used = [];
    public bool RefuseDelete;
    private int _next = 100;
    public IReadOnlyList<DriverPackage> List() => Packages.ToList();
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? InUse() => Used;
    public void Export(string publishedName, string directory)
    {
        var p = Packages.Single(x => x.PublishedName == publishedName);
        File.WriteAllText(Path.Combine(directory, p.OriginalName), "[Version]");
        File.WriteAllText(Path.Combine(directory, "driver.sys"), "bin");
    }
    public void Delete(string publishedName)
    {
        if (RefuseDelete) throw new InvalidOperationException("One or more devices are presently installed using the specified INF.");
        Packages.RemoveAll(p => p.PublishedName == publishedName);
    }
    public void Add(string infPath)
    {
        var orig = Path.GetFileName(infPath);
        var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(infPath)!, "nexus-manifest.json"))).RootElement;
        Packages.Add(new DriverPackage($"oem{_next++}.inf", orig, manifest.GetProperty("Provider").GetString()!, "Impressoras",
            manifest.GetProperty("ClassGuid").GetString(), manifest.GetProperty("VersionText").GetString()!, null, null, "s"));
    }
    public void ExportAll(string directory) => File.WriteAllText(Path.Combine(directory, "all.txt"), "ok");
}

internal sealed class FakeRestore : IRestorePointBackend
{
    public int Status;
    public bool Skip; // Windows "aceita" mas não cria (limite de 24 h)
    public readonly List<RestorePointInfo> Points = [];
    public DateTime Now = new(2026, 10, 31, 12, 0, 0);
    public int Create(string description)
    {
        if (Status != 0) return Status;
        if (!Skip) Points.Add(new RestorePointInfo(Points.Count + 1, description, Now, 12));
        return 0;
    }
    public RestorePointList List() => new(Points.ToList(), null);
}

internal sealed class FakeAppx : IAppxBackend
{
    public readonly Dictionary<string, AppxPackageInfo> Installed = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> ManifestsOnDisk = new(StringComparer.OrdinalIgnoreCase);
    public string? LastSid;
    public AppxPackageInfo? Find(string name, string? userSid) { LastSid = userSid; return Installed.GetValueOrDefault(name); }
    public void Remove(string packageFullName, string? userSid)
    {
        LastSid = userSid;
        var k = Installed.Single(kv => kv.Value.PackageFullName == packageFullName).Key;
        Installed.Remove(k);
    }
    public void Register(string manifestPath)
    {
        // Caminho do Windows (barras invertidas) também quando o teste roda no Linux.
        var name = System.Text.RegularExpressions.Regex.Match(manifestPath, @"([A-Za-z0-9.\-]+)_[0-9]").Groups[1].Value;
        Installed[name] = Pkg(name);
    }
    public bool ManifestExists(string manifestPath) => ManifestsOnDisk.Contains(manifestPath);
    public static AppxPackageInfo Pkg(string name, bool fw = false) =>
        new(name, $"{name}_1.0.0.0_x64__8wekyb3d8bbwe", $"{name}_8wekyb3d8bbwe", "CN=Microsoft", "1.0.0.0",
            $@"C:\Program Files\WindowsApps\{name}_1.0.0.0_x64__8wekyb3d8bbwe", fw, false, "Store");
}

[Trait("Category", "Unit")]
public sealed class SystemToolsHandlerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-sys-").FullName;
    private static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HandlerEnvironment Env(bool otherAccount = false) => new(new InMemoryRegistryBackend(), _dir, otherAccount, true);

    private static ActionInstance Instance(string id, Dictionary<string, string>? args = null, string? sid = null)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == id);
        var validated = entry.ValidateUserArguments(args ?? [], out var reason);
        Assert.True(validated is not null, reason);
        var inst = entry.TryInstantiate(validated!, out reason);
        Assert.True(inst is not null, reason);
        return sid is null ? inst! : UserProfileScope.Bind(inst!, sid);
    }

    private static (IReadOnlyList<ExecutionResult> Results, InMemoryJournal Journal, ActionExecutor Exec, Guid Run) Execute(IActionHandler handler, ActionInstance action)
    {
        var handlers = new HandlerRegistry().Add(handler);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = exec.BeginRun("teste", "1");
        var plan = new DesiredStateResolver(handlers).Resolve([action], T.Ctx);
        return (exec.Execute(run, plan, T.Ctx), journal, exec, run);
    }

    // ------------------------------------------------------------------ catálogo

    [Fact]
    public void Catalog_loads_and_every_kind_is_registered()
    {
        var mine = Catalog.Actions.Where(a => a.Definition.Id.StartsWith("system.", StringComparison.Ordinal)).ToList();
        Assert.Equal(7, mine.Count);
        Assert.Equal(Catalog.Actions.Count, Catalog.Actions.Select(a => a.Definition.Key).Distinct().Count());
        var kinds = SystemToolsHandlers.Create(Env()).Select(h => h.HandlerKind).ToHashSet();
        Assert.All(mine, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        Assert.All(mine, a => Assert.False(string.IsNullOrWhiteSpace(a.Definition.Mechanism) || string.IsNullOrWhiteSpace(a.Revert) || a.Definition.Evidence.Count == 0));
        Assert.Equal(kinds.Count, mine.Select(a => a.Definition.HandlerKind).Distinct().Count()); // nenhum handler sem ação
        Assert.All(kinds, k => Assert.StartsWith("System", k, StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_user_parameters_reject_bad_input()
    {
        var drv = Catalog.Actions.Single(a => a.Definition.Id == "system.driver.package-remove");
        Assert.Null(drv.ValidateUserArguments(new Dictionary<string, string> { ["driverInf"] = @"..\..\x.inf" }, out _));
        Assert.Null(drv.ValidateUserArguments(new Dictionary<string, string> { ["driverInf"] = "C:\\Windows\\inf\\oem1.inf" }, out _));
        var appx = Catalog.Actions.Single(a => a.Definition.Id == "system.appx.remove-user");
        Assert.Null(appx.ValidateUserArguments(new Dictionary<string, string> { ["appxName"] = "x'; rm -r" }, out _));
        var wifi = Catalog.Actions.Single(a => a.Definition.Id == "system.wifi.profile-remove");
        Assert.NotNull(wifi.ValidateUserArguments(new Dictionary<string, string> { ["wifiProfile"] = "Café: centro" }, out _));
        Assert.Null(wifi.ValidateUserArguments(new Dictionary<string, string> { ["wifiProfile"] = "a\u0001b" }, out _));
    }

    // ------------------------------------------------------------------ efêmeras

    [Fact]
    public void Dns_flush_is_ephemeral_and_revert_is_honest()
    {
        var net = new FakeNetwork();
        var handler = new DnsFlushHandler(net);
        var (results, journal, exec, run) = Execute(handler, Instance("system.network.dns-flush"));
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(results).Outcome);
        Assert.Equal(1, net.Dns);
        Assert.Null(Assert.Single(journal.Entries(run)).Original); // sem backup: não há estado anterior
        // Desfazer: "nada a desfazer" explícito, sem chamar o handler (nada é escrito, nada é executado de novo).
        var undo = Assert.Single(exec.RevertRun(run));
        Assert.Equal(RevertOutcome.NothingToRevert, undo.Outcome);
        Assert.Equal(ActionExecutor.EphemeralNothingToUndo, undo.Detail);
        Assert.Equal(1, net.Dns);
        // Repetir logo depois é executar de novo (não há marcador de "feito há poucos segundos").
        Assert.Equal(ExecutionOutcome.Verified, Execute(handler, Instance("system.network.dns-flush")).Results[0].Outcome);
        Assert.Equal(2, net.Dns);
    }

    [Fact]
    public void Ephemeral_actions_are_declared_ephemeral_in_the_catalog()
    {
        foreach (var id in new[] { "system.network.dns-flush", "system.network.arp-flush", "system.driver.backup-all", "system.restore.create-point" })
            Assert.Equal(PersistenceKind.Ephemeral, Catalog.Actions.Single(a => a.Definition.Id == id).Definition.Persistence);
        // Uma ação efêmera declarada como Persistent é recusada pelo handler (o executor precisa saber que não há estado).
        var wrong = Instance("system.network.dns-flush");
        wrong = wrong with { Definition = wrong.Definition with { Persistence = PersistenceKind.Persistent } };
        Assert.Equal(ApplicabilityKind.Blocked, new DnsFlushHandler(new FakeNetwork()).Assess(wrong, T.Ctx).Kind);
    }

    [Fact]
    public void Old_journal_entry_with_the_15s_marker_still_reverts_without_writing()
    {
        // Diário de antes da rodada 2: Persistent, original "Ephemeral:pronto", desejado "Ephemeral:executado".
        var net = new FakeNetwork();
        var handlers = new HandlerRegistry().Add(new DnsFlushHandler(net));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = exec.BeginRun("antigo", "1");
        var inst = Instance("system.network.dns-flush");
        inst = inst with { Definition = inst.Definition with { Persistence = PersistenceKind.Persistent } };
        var id = journal.AddPlanned(run, 0, inst);
        journal.Transition(id, JournalState.BackedUp, original: StateSnapshot.Of("Ephemeral", "pronto"));
        journal.Transition(id, JournalState.Applying);
        journal.Transition(id, JournalState.Applied);
        journal.Transition(id, JournalState.Verified, observed: StateSnapshot.Of("Ephemeral", "executado"));
        Assert.Equal(RevertOutcome.AlreadyOriginal, exec.Revert(id).Outcome);
        Assert.Equal(0, net.Dns);
    }

    [Fact]
    public void Ephemeral_failure_is_reported_not_verified()
    {
        var net = new FakeNetwork { Fail = true };
        var (results, journal, _, run) = Execute(new DnsFlushHandler(net), Instance("system.network.dns-flush"));
        Assert.Equal(ExecutionOutcome.Failed, results[0].Outcome);
        Assert.Contains("falhou", results[0].Detail, StringComparison.Ordinal);
        // Falha sem backup não fica pendente para a recuperação.
        Assert.Empty(RecoveryService.FindInterrupted(journal));
        Assert.Equal(JournalState.Failed, Assert.Single(journal.Entries(run)).State);
    }

    [Fact]
    public void Restore_point_accepts_new_or_recent_and_explains_failures()
    {
        var sr = new FakeRestore();
        var h = new RestorePointHandler(sr, () => sr.Now);
        Assert.Equal(ExecutionOutcome.Verified, Execute(h, Instance("system.restore.create-point")).Results[0].Outcome);
        Assert.Single(sr.Points);

        // Limite de 24 h com um ponto de 2 min atrás (ex.: o do aplicador): aceito.
        sr.Skip = true;
        sr.Now = sr.Now.AddMinutes(2);
        Assert.Equal(ExecutionOutcome.Verified, Execute(new RestorePointHandler(sr, () => sr.Now), Instance("system.restore.create-point")).Results[0].Outcome);

        // Limite de 24 h e nada recente: falha com explicação.
        sr.Now = sr.Now.AddHours(3);
        var r = Execute(new RestorePointHandler(sr, () => sr.Now), Instance("system.restore.create-point")).Results[0];
        Assert.Equal(ExecutionOutcome.Failed, r.Outcome);
        Assert.Contains("24 horas", r.Detail, StringComparison.Ordinal);

        sr.Status = 1058;
        r = Execute(new RestorePointHandler(sr, () => sr.Now), Instance("system.restore.create-point")).Results[0];
        Assert.Contains("desligada", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Driver_backup_all_exports_to_dated_folder()
    {
        var store = new FakeDriverStore();
        var r = Execute(new DriverBackupAllHandler(store, _dir), Instance("system.driver.backup-all")).Results[0];
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        Assert.Single(Directory.GetDirectories(DriverPackageHandler.BackupRoot(_dir), "todos-*"));
    }

    // ------------------------------------------------------------------ Wi-Fi

    private static string Xml(string name) => $"<?xml version=\"1.0\"?><WLANProfile><name>{name}</name><keyMaterial>segredo</keyMaterial></WLANProfile>";

    [Fact]
    public void Wifi_remove_backs_up_encrypted_and_restores()
    {
        var wifi = new FakeWifi();
        wifi.Profiles[(wifi.If1, "Casa Velha")] = (Xml("Casa Velha"), false, false);
        var h = new WifiProfileHandler(wifi, new FakeProtector(), _dir, otherAccount: false);
        var action = Instance("system.wifi.profile-remove", new() { ["wifiProfile"] = "Casa Velha" });

        var (results, _, exec, run) = Execute(h, action);
        Assert.Equal(ExecutionOutcome.Verified, results[0].Outcome);
        Assert.Empty(wifi.Profiles);
        var file = File.ReadAllText(h.BackupPath("Casa Velha"));
        Assert.DoesNotContain("segredo", file, StringComparison.Ordinal); // senha nunca em texto no disco

        Assert.Equal(RevertOutcome.Reverted, Assert.Single(exec.RevertRun(run)).Outcome);
        Assert.Contains("segredo", wifi.Profiles[(wifi.If1, "Casa Velha")].Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Wifi_backup_folder_is_restricted_before_the_secret_is_written()
    {
        // Revisão de segurança: como serviço, o backup usa DPAPI da máquina (qualquer conta local decifra) e ficava numa pasta
        // legível por Usuários. A pasta agora é restringida antes de o primeiro arquivo ser gravado; falha = nada removido.
        var wifi = new FakeWifi();
        wifi.Profiles[(wifi.If1, "Casa")] = (Xml("Casa"), false, false);
        var restricted = new List<(string Dir, int FilesAtThatMoment)>();
        var h = new WifiProfileHandler(wifi, new FakeProtector(), _dir, otherAccount: false,
            restrictBackupDirectory: d => restricted.Add((d, Directory.GetFiles(d).Length)));
        h.Apply(Instance("system.wifi.profile-remove", new() { ["wifiProfile"] = "Casa" }));
        var (dir, files) = Assert.Single(restricted);
        Assert.Equal(Path.GetDirectoryName(h.BackupPath("Casa")), dir);
        Assert.Equal(0, files);
        Assert.Empty(wifi.Profiles);

        wifi.Profiles[(wifi.If1, "Outra")] = (Xml("Outra"), false, false);
        var failing = new WifiProfileHandler(wifi, new FakeProtector(), _dir, otherAccount: false,
            restrictBackupDirectory: _ => throw new UnauthorizedAccessException("sem permissão"));
        Assert.Throws<UnauthorizedAccessException>(() => failing.Apply(Instance("system.wifi.profile-remove", new() { ["wifiProfile"] = "Outra" })));
        Assert.True(wifi.Profiles.ContainsKey((wifi.If1, "Outra"))); // sem backup protegido, nada é removido
        Assert.False(File.Exists(failing.BackupPath("Outra")));
    }

    [Fact]
    public void Wifi_group_policy_missing_and_other_account_rules()
    {
        var wifi = new FakeWifi();
        wifi.Profiles[(wifi.If1, "Corp")] = (Xml("Corp"), false, true);
        wifi.Profiles[(wifi.If1, "Meu")] = (Xml("Meu"), true, false);
        var h = new WifiProfileHandler(wifi, new FakeProtector(), _dir, otherAccount: false);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Instance("system.wifi.profile-remove", new() { ["wifiProfile"] = "Corp" }), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(Instance("system.wifi.profile-remove", new() { ["wifiProfile"] = "Nada" }), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(Instance("system.wifi.profile-remove", new() { ["wifiProfile"] = "Meu" }), T.Ctx).Kind);
        var asService = new WifiProfileHandler(wifi, new FakeProtector(), _dir, otherAccount: true);
        Assert.Equal(ApplicabilityKind.Blocked, asService.Assess(Instance("system.wifi.profile-remove", new() { ["wifiProfile"] = "Meu" }), T.Ctx).Kind);
        wifi.Unavailable = true;
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(Instance("system.wifi.profile-remove", new() { ["wifiProfile"] = "Meu" }), T.Ctx).Kind);
    }

    [Fact]
    public void Wifi_restore_without_backup_is_refused_before_writing()
    {
        var h = new WifiProfileHandler(new FakeWifi(), new FakeProtector(), _dir, false);
        var action = Instance("system.wifi.profile-remove", new() { ["wifiProfile"] = "Sumiu" });
        Assert.NotNull(h.CheckRestore(action, StateSnapshot.Of(WifiProfileHandler.StateType, PresenceState.Present)));
        Assert.Null(h.CheckRestore(action, StateSnapshot.Of(WifiProfileHandler.StateType, PresenceState.Removed)));
    }

    // ------------------------------------------------------------------ drivers

    private const string Printer = "{4d36e979-e325-11ce-bfc1-08002be10318}";

    [Fact]
    public void Driver_remove_exports_then_deletes_and_restore_reinstalls_under_new_name()
    {
        var store = new FakeDriverStore();
        store.Packages.Add(new DriverPackage("oem20.inf", "prnhp001.inf", "HP", "Impressoras", Printer, "10.0.19041.1", new Version(10, 0, 19041, 1), null, "s"));
        var h = new DriverPackageHandler(store, _dir);
        var action = Instance("system.driver.package-remove", new() { ["driverInf"] = "oem20.inf" });

        var (results, _, exec, run) = Execute(h, action);
        Assert.Equal(ExecutionOutcome.Verified, results[0].Outcome);
        Assert.Empty(store.Packages);
        Assert.True(File.Exists(Path.Combine(h.BackupDirectory("oem20.inf"), "prnhp001.inf")));

        Assert.Equal(RevertOutcome.Reverted, Assert.Single(exec.RevertRun(run)).Outcome);
        Assert.Equal("oem100.inf", Assert.Single(store.Packages).PublishedName); // reinstalado com outro nome, reconhecido pelo manifesto
    }

    [Fact]
    public void Driver_in_use_is_blocked_and_windows_refusal_is_compensated()
    {
        var store = new FakeDriverStore();
        store.Packages.Add(new DriverPackage("oem20.inf", "prn.inf", "HP", "Impressoras", Printer, "1.0", new Version(1, 0), null, "s"));
        store.Used = new() { ["oem20.inf"] = ["HP LaserJet"] };
        var h = new DriverPackageHandler(store, _dir);
        var action = Instance("system.driver.package-remove", new() { ["driverInf"] = "oem20.inf" });
        var r = Execute(h, action).Results[0];
        Assert.Equal(ExecutionOutcome.Blocked, r.Outcome);
        Assert.Contains("HP LaserJet", r.Detail, StringComparison.Ordinal);

        store.Used = null;
        Assert.Equal(ExecutionOutcome.Blocked, Execute(h, action).Results[0].Outcome);

        // O Windows recusa (pnputil sem /force): nada removido, resultado honesto.
        store.Used = [];
        store.RefuseDelete = true;
        r = Execute(h, action).Results[0];
        Assert.Equal(ExecutionOutcome.FailedAndCompensated, r.Outcome);
        Assert.Single(store.Packages);
    }

    // ------------------------------------------------------------------ apps do Windows

    [Fact]
    public void Appx_remove_and_restore_when_package_still_on_disk()
    {
        var appx = new FakeAppx();
        appx.Installed["Microsoft.BingNews"] = FakeAppx.Pkg("Microsoft.BingNews");
        appx.ManifestsOnDisk.Add(Path.Combine(appx.Installed["Microsoft.BingNews"].InstallLocation!, "AppxManifest.xml"));
        var h = new AppxPackageHandler(appx, _dir, otherAccount: false);
        var (results, _, exec, run) = Execute(h, Instance("system.appx.remove-user", new() { ["appxName"] = "Microsoft.BingNews" }));
        Assert.Equal(ExecutionOutcome.Verified, results[0].Outcome);
        Assert.Empty(appx.Installed);
        Assert.Equal(RevertOutcome.Reverted, Assert.Single(exec.RevertRun(run)).Outcome);
        Assert.True(appx.Installed.ContainsKey("Microsoft.BingNews"));
    }

    [Fact]
    public void Appx_restore_is_limited_and_says_store()
    {
        var appx = new FakeAppx();
        appx.Installed["king.com.CandyCrushSaga"] = FakeAppx.Pkg("king.com.CandyCrushSaga");
        var h = new AppxPackageHandler(appx, _dir, otherAccount: false);
        var (results, _, exec, run) = Execute(h, Instance("system.appx.remove-user", new() { ["appxName"] = "king.com.CandyCrushSaga" }));
        Assert.Equal(ExecutionOutcome.Verified, results[0].Outcome);
        var revert = Assert.Single(exec.RevertRun(run));
        Assert.Equal(RevertOutcome.Failed, revert.Outcome);
        Assert.Contains("ms-windows-store://pdp/?PFN=king.com.CandyCrushSaga_8wekyb3d8bbwe", revert.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Appx_protected_unknown_and_other_account()
    {
        var appx = new FakeAppx();
        appx.Installed["Microsoft.WindowsStore"] = FakeAppx.Pkg("Microsoft.WindowsStore");
        appx.Installed["Contoso.App"] = FakeAppx.Pkg("Contoso.App");
        appx.Installed["Microsoft.BingNews"] = FakeAppx.Pkg("Microsoft.BingNews");
        var h = new AppxPackageHandler(appx, _dir, otherAccount: false);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Instance("system.appx.remove-user", new() { ["appxName"] = "Microsoft.WindowsStore" }), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Instance("system.appx.remove-user", new() { ["appxName"] = "Contoso.App" }), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(Instance("system.appx.remove-user", new() { ["appxName"] = "Microsoft.GetHelp" }), T.Ctx).Kind);

        // Aplicador como outra conta: sem SID do usuário, nada; com SID, remove para aquele usuário e não reinstala por engano.
        var svc = new AppxPackageHandler(appx, _dir, otherAccount: true);
        Assert.Equal(ApplicabilityKind.Blocked, svc.Assess(Instance("system.appx.remove-user", new() { ["appxName"] = "Microsoft.BingNews" }), T.Ctx).Kind);
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        var bound = Instance("system.appx.remove-user", new() { ["appxName"] = "Microsoft.BingNews" }, sid);
        Assert.True(UserProfileScope.IsUserScoped(bound));
        var (results, _, exec, run) = Execute(svc, bound);
        Assert.Equal(ExecutionOutcome.Verified, results[0].Outcome);
        Assert.Equal(sid, appx.LastSid);
        var revert = Assert.Single(exec.RevertRun(run));
        Assert.Equal(RevertOutcome.Failed, revert.Outcome);
        Assert.Contains("sessão do próprio usuário", revert.Detail, StringComparison.Ordinal);
    }
}
