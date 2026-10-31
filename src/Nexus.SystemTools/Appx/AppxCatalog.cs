using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.SystemTools.Appx;

public enum AppxClass
{
    /// <summary>Fora da lista: o NEXUS não remove (não sabemos o que depende dele).</summary>
    Unknown,
    /// <summary>Promocional/pré-instalado, seguro de remover para o usuário.</summary>
    Safe,
    /// <summary>Útil para muita gente: removível, mas a confirmação avisa o que se perde.</summary>
    Warn,
    /// <summary>Nunca removido (Store, instalador de apps/winget, bibliotecas, segurança, componentes do sistema).</summary>
    Protected,
}

public sealed record AppxCatalogEntry(string Pattern, string Title, AppxClass Class, string Note);

/// <summary>Pacote Appx/MSIX lido de <c>Get-AppxPackage</c>.</summary>
public sealed record AppxPackageInfo(
    string Name,
    string PackageFullName,
    string PackageFamilyName,
    string Publisher,
    string Version,
    string? InstallLocation,
    bool IsFramework,
    bool NonRemovable,
    string SignatureKind);

/// <summary>Lista fechada do que o debloat pode remover. O que não está aqui não é removido.</summary>
public static partial class AppxCatalog
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{2,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9.\-]+_[A-Za-z0-9.\-]+_[A-Za-z0-9]*_[A-Za-z0-9.\-~]*_[a-z0-9]{13}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FullNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9.\-]+_[a-z0-9]{13}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FamilyPattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);
    public static bool IsValidFullName(string? name) => name is not null && FullNamePattern().IsMatch(name);
    public static bool IsValidFamilyName(string? name) => name is not null && FamilyPattern().IsMatch(name);

    public static readonly IReadOnlyList<AppxCatalogEntry> Protected =
    [
        new("Microsoft.WindowsStore", "Microsoft Store", AppxClass.Protected, "Sem a Store não dá para reinstalar apps nem receber atualizações deles."),
        new("Microsoft.StorePurchaseApp", "Compras da Store", AppxClass.Protected, "Usado pela Store."),
        new("Microsoft.DesktopAppInstaller", "Instalador de Aplicativo (winget)", AppxClass.Protected, "Contém o winget, usado pelo NEXUS e por muitos instaladores."),
        new("Microsoft.VCLibs.*", "Bibliotecas Visual C++ (VCLibs)", AppxClass.Protected, "Biblioteca de que outros apps dependem."),
        new("Microsoft.NET.*", "Runtime .NET", AppxClass.Protected, "Biblioteca de que outros apps dependem."),
        new("Microsoft.UI.Xaml.*", "WinUI (UI.Xaml)", AppxClass.Protected, "Biblioteca de interface de que outros apps dependem."),
        new("Microsoft.WindowsAppRuntime.*", "Windows App SDK", AppxClass.Protected, "Biblioteca de que outros apps dependem."),
        new("Microsoft.Services.Store.Engagement", "Serviços da Store", AppxClass.Protected, "Biblioteca de que outros apps dependem."),
        new("Microsoft.SecHealthUI", "Segurança do Windows", AppxClass.Protected, "Interface do Microsoft Defender."),
        new("Microsoft.Windows.SecHealthUI", "Segurança do Windows", AppxClass.Protected, "Interface do Microsoft Defender."),
        new("Microsoft.XboxIdentityProvider", "Provedor de identidade Xbox", AppxClass.Protected, "Muitos jogos de PC usam para entrar na conta Xbox."),
        new("Microsoft.Xbox.TCUI", "Xbox TCUI", AppxClass.Protected, "Muitos jogos de PC usam para entrar na conta Xbox."),
        new("Microsoft.AAD.BrokerPlugin", "Conta corporativa/escolar", AppxClass.Protected, "Componente de login do Windows."),
        new("Microsoft.AccountsControl", "Contas", AppxClass.Protected, "Componente de login do Windows."),
        new("Microsoft.Windows.*Experience*", "Componentes do shell", AppxClass.Protected, "Parte do Windows (menu Iniciar, shell)."),
        new("MicrosoftWindows.*", "Componentes do Windows", AppxClass.Protected, "Parte do Windows."),
        new("Windows.*", "Componentes do Windows", AppxClass.Protected, "Parte do Windows."),
        new("Microsoft.MicrosoftEdge*", "Microsoft Edge", AppxClass.Protected, "Componente do Windows; WebView2 depende dele."),
        new("Microsoft.Win32WebViewHost", "WebView", AppxClass.Protected, "Componente do Windows."),
        new("Microsoft.*Extension", "Extensões de mídia (codecs)", AppxClass.Protected, "Codecs de imagem e vídeo (HEIF, HEVC, WebP, VP9, AV1)."),
        new("Microsoft.LanguageExperiencePack*", "Pacote de idioma", AppxClass.Protected, "Idioma da interface do Windows."),
    ];

    public static readonly IReadOnlyList<AppxCatalogEntry> Removable =
    [
        new("king.com.*", "Jogos King (Candy Crush etc.)", AppxClass.Safe, "Jogo promocional pré-instalado."),
        new("BytedancePte.Ltd.TikTok", "TikTok", AppxClass.Safe, "App promocional pré-instalado."),
        new("Clipchamp.Clipchamp", "Clipchamp", AppxClass.Safe, "Editor de vídeo pré-instalado."),
        new("Microsoft.BingNews", "Microsoft Notícias", AppxClass.Safe, "Notícias da Microsoft."),
        new("Microsoft.BingWeather", "Clima (MSN)", AppxClass.Safe, "App de clima."),
        new("Microsoft.BingFinance", "Finanças (MSN)", AppxClass.Safe, "App descontinuado."),
        new("Microsoft.BingSports", "Esportes (MSN)", AppxClass.Safe, "App descontinuado."),
        new("Microsoft.BingSearch", "Pesquisa do Bing", AppxClass.Safe, "App de pesquisa do Bing."),
        new("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection", AppxClass.Safe, "Jogo com anúncios."),
        new("Microsoft.GetHelp", "Obter Ajuda", AppxClass.Safe, "Atalho para o suporte online da Microsoft."),
        new("Microsoft.Getstarted", "Dicas", AppxClass.Safe, "Dicas do Windows."),
        new("Microsoft.WindowsFeedbackHub", "Hub de Comentários", AppxClass.Safe, "Envio de comentários à Microsoft."),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)", AppxClass.Safe, "Atalho para o Office online; não remove o Office instalado."),
        new("Microsoft.SkypeApp", "Skype", AppxClass.Safe, "Serviço encerrado pela Microsoft em 2025."),
        new("Microsoft.Microsoft3DViewer", "Visualizador 3D", AppxClass.Safe, "Pouco usado."),
        new("Microsoft.Print3D", "Print 3D", AppxClass.Safe, "Descontinuado."),
        new("Microsoft.MixedReality.Portal", "Portal de Realidade Misturada", AppxClass.Safe, "Só para óculos Windows Mixed Reality (descontinuados)."),
        new("Microsoft.People", "Pessoas", AppxClass.Safe, "Contatos (integrado ao Outlook)."),
        new("Microsoft.Messaging", "Mensagens", AppxClass.Safe, "Descontinuado."),
        new("Microsoft.OneConnect", "Planos Móveis", AppxClass.Safe, "Planos de dados móveis."),
        new("Microsoft.Wallet", "Carteira", AppxClass.Safe, "Descontinuado."),
        new("Microsoft.WindowsMaps", "Mapas", AppxClass.Safe, "Mapas offline."),
        new("Microsoft.ZuneVideo", "Filmes e TV", AppxClass.Safe, "Player e loja de vídeos."),
        new("Microsoft.Todos", "Microsoft To Do", AppxClass.Safe, "Lista de tarefas."),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", AppxClass.Safe, "Automação de tarefas."),
        new("Microsoft.549981C3F5F10", "Cortana", AppxClass.Safe, "Assistente descontinuado."),
        new("Microsoft.MicrosoftJournal", "Microsoft Journal", AppxClass.Safe, "Anotações à caneta."),
        new("Microsoft.Copilot", "Copilot", AppxClass.Safe, "Assistente de IA da Microsoft."),
        new("SpotifyAB.SpotifyMusic", "Spotify", AppxClass.Safe, "Pré-instalado; reinstale pela Store se usar."),
        new("Disney.37853FC22B2CE", "Disney+", AppxClass.Safe, "App promocional pré-instalado."),
        new("AmazonVideo.PrimeVideo", "Prime Video", AppxClass.Safe, "App promocional pré-instalado."),
        new("Facebook.*", "Facebook/Instagram", AppxClass.Safe, "App promocional pré-instalado."),
        new("5A894077.McAfeeSecurity", "McAfee (promocional)", AppxClass.Safe, "Antivírus de teste pré-instalado; o Defender continua protegendo."),
        new("Microsoft.ZuneMusic", "Media Player / Groove", AppxClass.Warn, "No Windows 11 é o Media Player: sem ele, áudio e vídeo abrem em outro programa."),
        new("Microsoft.YourPhone", "Link to Phone (Vincular ao Celular)", AppxClass.Warn, "Integra o celular ao PC."),
        new("Microsoft.WindowsCommunicationsApps", "Email e Calendário", AppxClass.Warn, "App de e-mail antigo do Windows."),
        new("Microsoft.OutlookForWindows", "Outlook (novo)", AppxClass.Warn, "Cliente de e-mail."),
        new("Microsoft.MicrosoftStickyNotes", "Notas Autoadesivas", AppxClass.Warn, "Notas sincronizadas com a conta Microsoft."),
        new("MicrosoftCorporationII.QuickAssist", "Assistência Rápida", AppxClass.Warn, "Suporte remoto; útil para receber ajuda."),
        new("Microsoft.GamingApp", "Xbox", AppxClass.Warn, "Necessário para o PC Game Pass e jogos da loja Xbox. Remova só se não usa."),
        new("Microsoft.XboxApp", "Xbox Console Companion", AppxClass.Warn, "App antigo do Xbox."),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", AppxClass.Warn, "Barra de jogo (Win+G), gravação e capturas. Remova só se não usa."),
        new("Microsoft.XboxGameOverlay", "Sobreposição de jogo Xbox", AppxClass.Warn, "Usado pela Game Bar."),
        new("Microsoft.XboxSpeechToTextOverlay", "Fala para texto Xbox", AppxClass.Warn, "Usado pela Game Bar."),
        new("Microsoft.Windows.Photos", "Fotos", AppxClass.Warn, "Visualizador de imagens padrão: sem ele, imagens abrem em outro programa."),
        new("Microsoft.WindowsCalculator", "Calculadora", AppxClass.Warn, "A calculadora do Windows."),
        new("Microsoft.WindowsCamera", "Câmera", AppxClass.Warn, "App da webcam."),
        new("Microsoft.WindowsSoundRecorder", "Gravador de Som", AppxClass.Warn, "Gravação de áudio."),
        new("Microsoft.WindowsAlarms", "Relógio", AppxClass.Warn, "Alarmes e temporizador."),
        new("Microsoft.Paint", "Paint", AppxClass.Warn, "Editor de imagens simples."),
        new("Microsoft.ScreenSketch", "Ferramenta de Captura", AppxClass.Warn, "Print de tela (Win+Shift+S)."),
    ];

    private static bool Matches(string pattern, string name)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Classe do pacote pelo nome. Proteção vence a lista de removíveis.</summary>
    public static (AppxClass Class, AppxCatalogEntry? Entry) Classify(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Protected.FirstOrDefault(e => Matches(e.Pattern, name)) is { } p) return (AppxClass.Protected, p);
        if (Removable.FirstOrDefault(e => Matches(e.Pattern, name)) is { } r) return (r.Class, r);
        return (AppxClass.Unknown, null);
    }

    /// <summary>Motivo para NÃO remover, ou null se pode remover para o usuário.</summary>
    public static string? CheckRemoval(AppxPackageInfo pkg)
    {
        ArgumentNullException.ThrowIfNull(pkg);
        if (!IsValidName(pkg.Name)) return "Nome de pacote inválido.";
        if (pkg.IsFramework) return "Biblioteca usada por outros apps: não é removida.";
        if (pkg.NonRemovable) return "O Windows marca este pacote como não removível.";
        if (string.Equals(pkg.SignatureKind, "System", StringComparison.OrdinalIgnoreCase)) return "Componente do sistema: não é removido.";
        return Classify(pkg.Name) switch
        {
            (AppxClass.Protected, var e) => $"Protegido: {e!.Note}",
            (AppxClass.Unknown, _) => "Fora da lista de apps que o NEXUS sabe remover com segurança.",
            _ => null,
        };
    }

    public static string StoreLink(string familyName) => "ms-windows-store://pdp/?PFN=" + familyName;
}

/// <summary>Leitura do JSON de <c>Get-AppxPackage | Select-Object … | ConvertTo-Json</c> (lista, objeto único ou vazio).</summary>
public static class AppxParsers
{
    private static readonly string[] SignatureNames = ["None", "Developer", "Enterprise", "Store", "System"];

    public static IReadOnlyList<AppxPackageInfo> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        json = json.Trim();
        if (json.Length == 0) return [];
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        IEnumerable<JsonElement> items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object => [root],
            _ => [],
        };
        var list = new List<AppxPackageInfo>();
        foreach (var e in items)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var name = Str(e, "Name");
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new AppxPackageInfo(name, Str(e, "PackageFullName") ?? "", Str(e, "PackageFamilyName") ?? "", Str(e, "Publisher") ?? "",
                Version(e), Str(e, "InstallLocation"), Bool(e, "IsFramework"), Bool(e, "NonRemovable"), Signature(e)));
        }
        return list;
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var p) && (p.ValueKind == JsonValueKind.True ||
        (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b) && b));

    private static string Version(JsonElement e)
    {
        if (!e.TryGetProperty("Version", out var v)) return "";
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        if (v.ValueKind == JsonValueKind.Object) // System.Version serializado como objeto (Major, Minor, Build, Revision)
        {
            int P(string n) => v.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0;
            return $"{P("Major")}.{P("Minor")}.{P("Build")}.{P("Revision")}";
        }
        return "";
    }

    private static string Signature(JsonElement e)
    {
        if (!e.TryGetProperty("SignatureKind", out var s)) return "";
        if (s.ValueKind == JsonValueKind.String) return s.GetString() ?? "";
        if (s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var i) && i >= 0 && i < SignatureNames.Length) return SignatureNames[i];
        return "";
    }
}
