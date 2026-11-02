namespace Nexus.Desktop.Views.Shell;

/// <summary>
/// Mapas da casca (lógica pura, sem WPF, testada em DesignSystemTests): página → ícone de linha, página → ilustração,
/// achado do diagnóstico → página que resolve, e a ordem dos grupos da navegação. As páginas não precisam saber disso:
/// a casca resolve pelo nome do tipo do ViewModel (ex.: "CleaningPage").
/// </summary>
public static class ShellMap
{
    /// <summary>Ícone padrão quando a página ainda não tem um próprio.</summary>
    public const string DefaultIcon = "Icon.Page";

    /// <summary>Ilustração padrão quando a página ainda não tem uma própria.</summary>
    public const string DefaultIllustration = "Illo.Pc";

    /// <summary>Ordem dos grupos da navegação lateral (por tarefa do jogador). Grupos desconhecidos vão para o fim.</summary>
    public static readonly IReadOnlyList<string> GroupOrder =
        ["Meu PC", "Assistente", "Automático", "Laboratório", "Otimizar", "Limpar", "Proteger", "Sistema", "Medir", "Registro"];

    private static readonly Dictionary<string, (string Icon, string Illustration)> Pages = new(StringComparer.Ordinal)
    {
        ["OverviewPage"] = ("Icon.Pc", "Illo.Pc"),
        ["HardwarePage"] = ("Icon.Hardware", "Illo.Pc"),
        ["DiagnosticsPage"] = ("Icon.Diagnostics", "Illo.Diagnostics"),
        ["AssistantPage"] = ("Icon.Assistant", "Illo.Assistant"),
        ["AutomationPage"] = ("Icon.Auto", "Illo.Automation"),
        ["CpuLabPage"] = ("Icon.Cpu", "Illo.Cpu"),
        ["GpuLabPage"] = ("Icon.Gpu", "Illo.Gpu"),
        ["MemoryLabPage"] = ("Icon.Ram", "Illo.Ram"),
        ["MemoryDoctorPage"] = ("Icon.MemoryDoctor", "Illo.MemoryDoctor"),
        ["CatalogPage"] = ("Icon.Catalog", "Illo.Settings"),
        ["PlanPage"] = ("Icon.Plan", "Illo.Settings"),
        ["BrokerPage"] = ("Icon.Apply", "Illo.Security"),
        ["CleaningPage"] = ("Icon.Clean", "Illo.Cleaning"),
        ["DiskPage"] = ("Icon.Disk", "Illo.Disk"),
        ["ProgramsPage"] = ("Icon.Programs", "Illo.Programs"),
        ["PrivacyPage"] = ("Icon.Privacy", "Illo.Privacy"),
        ["SecurityPage"] = ("Icon.Security", "Illo.Security"),
        ["ScannerPage"] = ("Icon.Scan", "Illo.Scanner"),
        ["StartupPage"] = ("Icon.Startup", "Illo.Startup"),
        ["ServicesPage"] = ("Icon.Services", "Illo.Services"),
        ["SystemToolsPage"] = ("Icon.Tools", "Illo.Network"),
        ["DriverUpdatesPage"] = ("Icon.Drivers", "Illo.Drivers"),
        ["MeasurePage"] = ("Icon.Measure", "Illo.Measure"),
        ["MonitorPage"] = ("Icon.Monitor", "Illo.Monitor"),
        ["HistoryPage"] = ("Icon.History", "Illo.History"),
        ["SettingsPage"] = ("Icon.Settings", "Illo.Settings"),
        ["CapabilitiesPage"] = ("Icon.Capabilities", "Illo.Diagnostics"),
    };

    /// <summary>Todas as páginas com mapa próprio (os testes conferem que cada chave existe nos dicionários XAML).</summary>
    public static IReadOnlyCollection<string> KnownPages => Pages.Keys;

    /// <summary>Chave do ícone (Themes/Icons.xaml) para o tipo da página.</summary>
    public static string IconFor(string? pageTypeName) =>
        pageTypeName is not null && Pages.TryGetValue(pageTypeName, out var p) ? p.Icon : DefaultIcon;

    /// <summary>Chave da ilustração (Assets/Illustrations) para o tipo da página.</summary>
    public static string IllustrationFor(string? pageTypeName) =>
        pageTypeName is not null && Pages.TryGetValue(pageTypeName, out var p) ? p.Illustration : DefaultIllustration;

    /// <summary>Posição do grupo na navegação (desconhecido = depois de todos).</summary>
    public static int GroupRank(string? group)
    {
        for (var i = 0; i < GroupOrder.Count; i++)
            if (string.Equals(GroupOrder[i], group, StringComparison.Ordinal)) return i;
        return GroupOrder.Count;
    }

    /// <summary>
    /// Página que resolve (ou explica) um achado do diagnóstico, pelo prefixo do id: "mem." → Memória RAM, "gpu." → Placa de
    /// vídeo, "disk." → Limpeza, "sec." → Segurança, "power."/"cpu." → Processador, "win." → O que dá pra ajustar.
    /// Sem mapa: a própria página Diagnóstico.
    /// </summary>
    public static string PageForFinding(string? findingId)
    {
        var id = findingId ?? "";
        if (id.StartsWith("mem.", StringComparison.Ordinal) || id == "owner.xmp") return "MemoryLabPage";
        if (id is "owner.rebar" or "owner.mux" or "owner.pcie") return "GpuLabPage";
        if (id.StartsWith("startup.", StringComparison.Ordinal)) return "StartupPage";
        if (id.StartsWith("gpu.", StringComparison.Ordinal)) return "GpuLabPage";
        if (id.StartsWith("disk.", StringComparison.Ordinal)) return "CleaningPage";
        if (id.StartsWith("sec.", StringComparison.Ordinal)) return "SecurityPage";
        if (id.StartsWith("power.", StringComparison.Ordinal) || id.StartsWith("cpu.", StringComparison.Ordinal)) return "CpuLabPage";
        if (id.StartsWith("win.", StringComparison.Ordinal)) return "CatalogPage";
        return "DiagnosticsPage";
    }

    private static readonly Dictionary<string, string> Actions = new(StringComparer.Ordinal)
    {
        ["MemoryLabPage"] = "Ver a memória",
        ["GpuLabPage"] = "Ver a placa de vídeo",
        ["CpuLabPage"] = "Ver o processador",
        ["CleaningPage"] = "Liberar espaço",
        ["DiskPage"] = "Ver o disco",
        ["SecurityPage"] = "Ver a segurança",
        ["CatalogPage"] = "Ver os ajustes",
        ["MeasurePage"] = "Medir uma partida",
        ["StartupPage"] = "Ver a inicialização",
        ["DiagnosticsPage"] = "Ver o diagnóstico",
    };

    /// <summary>Verbo do botão que abre a página ("Liberar espaço", "Ver a memória"). Sem mapa: "Abrir".</summary>
    public static string ActionFor(string? pageTypeName) =>
        pageTypeName is not null && Actions.TryGetValue(pageTypeName, out var a) ? a : "Abrir";

    /// <summary>Ícone do achado, pela mesma regra de <see cref="PageForFinding"/>.</summary>
    public static string IconForFinding(string? findingId) => IconFor(PageForFinding(findingId));
}
