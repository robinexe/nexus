using System.Text.RegularExpressions;
using Nexus.Programs.Inventory;

namespace Nexus.Programs.Uninstall;

/// <summary>Ambiente para interpretar desinstaladores (o real no Windows; falso nos testes).</summary>
/// <param name="FileExists">Existência de arquivo.</param>
/// <param name="Expand">Expansão de variáveis de ambiente.</param>
/// <param name="SystemDirectory">System32 (msiexec/rundll32).</param>
/// <param name="NexusDirectory">Pasta onde o NEXUS está instalado (para nunca se desinstalar no meio de um lote).</param>
public sealed record UninstallEnvironment(Func<string, bool> FileExists, Func<string, string> Expand, string SystemDirectory, string? NexusDirectory = null)
{
    public UninstallCommand? Parse(string? raw, out string? error) => UninstallCommandParser.Parse(raw, FileExists, Expand, SystemDirectory, out error);
}

public enum BulkEligibilityKind
{
    /// <summary>Pode entrar no lote (e em "selecionar visíveis").</summary>
    Allowed,
    /// <summary>Pode entrar, mas só marcado um a um e com uma confirmação extra que mostra o motivo.</summary>
    NeedsExtraConfirmation,
    /// <summary>Não entra no lote (motivo na linha).</summary>
    Blocked,
}

/// <summary>Se um programa pode entrar na desinstalação em massa, e por quê.</summary>
public sealed record BulkEligibility(BulkEligibilityKind Kind, string? Category = null, string? Reason = null)
{
    public static readonly BulkEligibility Allowed = new(BulkEligibilityKind.Allowed);
    public bool Selectable => Kind != BulkEligibilityKind.Blocked;
    public bool Critical => Kind == BulkEligibilityKind.NeedsExtraConfirmation;
}

/// <summary>
/// Regras do lote. Bloqueados (não marcáveis): app da Store (Configurações), "não removível" declarado pelo instalador, o
/// próprio NEXUS, componentes de atualização do Windows e desinstalador que o NEXUS não aceita executar (interpretador, sem
/// caminho, inexistente). Com confirmação extra (marcáveis só um a um): bibliotecas e drivers dos quais outros programas
/// dependem — Visual C++ Redistributable, .NET, DirectX, WebView2/Edge, drivers de vídeo, chipset, áudio e rede.
/// Tudo por nome/editor do registro: é uma lista conservadora, não uma garantia; a confirmação diz isso.
/// </summary>
public static partial class BulkUninstallPolicy
{
    private sealed record Rule(Regex Pattern, string Category, string Reason, bool Block = false);

    private const string RuntimeWhy = "Jogos e programas usam esta biblioteca; vários instalam a mesma versão e contam com ela. Sem ela, eles param de abrir até você reinstalar.";

    [GeneratedRegex(@"^Microsoft Visual C\+\+ .*(Redistributable|Runtime)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VcRedist();

    [GeneratedRegex(@"^(Microsoft )?(\.NET|ASP\.NET Core|Windows Desktop Runtime)\b|\.NET (Framework|Runtime|Desktop Runtime|Host)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNet();

    [GeneratedRegex(@"\bDirectX\b|^Microsoft XNA Framework|^Microsoft GameInput|^Vulkan Run ?Time|^OpenAL\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameRuntime();

    [GeneratedRegex(@"^Microsoft Edge( WebView2 Runtime)?\z|^Microsoft Edge WebView2", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Edge();

    [GeneratedRegex(@"^NVIDIA .*(Driver|PhysX|Display Container|Control Panel|FrameView SDK|USBC)|^AMD (Software|Radeon|Display Driver)|^Intel(\(R\)|®)? .*(Graphics|Arc)\b|^Intel(\(R\)|®)? Graphics", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GpuDriver();

    [GeneratedRegex(@"^AMD (Chipset|Ryzen Chipset|PSP|SMBus|GPIO|I2C|PCI|IOV)|^Intel(\(R\)|®)? .*(Chipset|Management Engine|Serial IO|Rapid Storage|Optane|Dynamic (Platform|Tuning)|Innovation Platform|Platform Framework|Wireless Bluetooth|Wi-?Fi|PROSet)|^Windows Driver Package", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Chipset();

    [GeneratedRegex(@"^Realtek .*(Audio|Ethernet|LAN|Card Reader|Driver|Bluetooth|Wireless)|Audio Driver|Ethernet Controller Driver|\bTouchpad Driver\b|^Synaptics|^ELAN|^Precision Touchpad", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceDriver();

    [GeneratedRegex(@"^(Microsoft Update Health Tools|Update for |Security Update for |Hotfix for |Windows PC Health Check\z|Microsoft Windows Desktop Runtime Prerequisites)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsComponent();

    [GeneratedRegex(@"^NEXUS(\b|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NexusName();

    private static readonly Rule[] Rules =
    [
        new(WindowsComponent(), "Componente do Windows", "Faz parte das atualizações do Windows. Remova (se precisar) por Configurações > Windows Update.", Block: true),
        new(VcRedist(), "Runtime do Visual C++", RuntimeWhy),
        new(DotNet(), ".NET", RuntimeWhy),
        new(GameRuntime(), "Runtime de jogos", RuntimeWhy),
        new(Edge(), "Edge / WebView2", "Muitos programas (inclusive do Windows e launchers de jogos) mostram telas com o WebView2. O Windows costuma reinstalar."),
        new(GpuDriver(), "Driver de vídeo", "Sem ele a tela volta para o driver básico do Windows: resolução limitada e jogos sem aceleração. Para trocar de versão, use a página Atualizar drivers ou o instalador do fabricante."),
        new(Chipset(), "Driver de chipset/placa", "Controla energia, armazenamento, Wi-Fi ou Bluetooth da placa. Remover pode desligar dispositivos até reinstalar o driver do fabricante."),
        new(DeviceDriver(), "Driver de dispositivo", "Som, rede ou touchpad podem parar até você reinstalar o driver do fabricante."),
    ];

    /// <summary>Avalia um programa. <paramref name="env"/> só é usado para conferir o desinstalador.</summary>
    public static BulkEligibility Evaluate(InstalledProgram program, UninstallEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(env);
        if (program.Scope == InstallScope.Store)
            return new(BulkEligibilityKind.Blocked, "App da Store", "Apps da Microsoft Store saem por Configurações > Aplicativos.");
        if (IsNexus(program, env.NexusDirectory))
            return new(BulkEligibilityKind.Blocked, "O próprio NEXUS", "É o NEXUS: desinstale por Configurações > Aplicativos, com ele fechado.");
        if (program.NoRemove)
            return new(BulkEligibilityKind.Blocked, "Não removível", "O próprio instalador marcou como não removível. Use Configurações > Aplicativos.");

        var rule = Rules.FirstOrDefault(r => r.Pattern.IsMatch(program.Name));
        if (rule is { Block: true }) return new(BulkEligibilityKind.Blocked, rule.Category, rule.Reason);

        if (env.Parse(program.UninstallString, out var error) is null)
            return new(BulkEligibilityKind.Blocked, "Desinstalador inválido", error ?? "Desinstalador inválido.");

        return rule is null ? BulkEligibility.Allowed : new(BulkEligibilityKind.NeedsExtraConfirmation, rule.Category, rule.Reason);
    }

    internal static bool IsNexus(InstalledProgram p, string? nexusDirectory)
    {
        if (NexusName().IsMatch(p.Name) && (p.Publisher is null || p.Publisher.Contains("NEXUS", StringComparison.OrdinalIgnoreCase) ||
                                            p.Publisher.Contains("robinexe", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (string.IsNullOrWhiteSpace(nexusDirectory)) return false;
        var dir = nexusDirectory.TrimEnd('\\', '/');
        bool Inside(string? path) => path is not null &&
            (path.Equals(dir, StringComparison.OrdinalIgnoreCase) || path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("\"" + dir + "\\", StringComparison.OrdinalIgnoreCase));
        return Inside(p.InstallLocation) || Inside(p.UninstallString?.Trim());
    }
}
