using System.Text.RegularExpressions;

namespace Nexus.DiskTools.SecureDelete;

/// <summary>
/// Regras de caminho da exclusão segura, independentes do disco (puras, testáveis em qualquer SO). Só caminhos absolutos do
/// Windows com letra de unidade, sem "..", sem nomes curtos 8.3 ("PROGRA~1"), sem fluxos alternativos (":"), sem nomes de
/// dispositivo, e fora das áreas do sistema, de programas, da raiz do perfil e dos dados do próprio NEXUS.
/// A validação no disco (link simbólico, junção no caminho, link físico, em uso, caminho final) fica no handler.
/// </summary>
public sealed partial class SecureDeletePolicy
{
    /// <summary>
    /// Regra do parâmetro <c>filePath</c> no catálogo (o broker valida antes do handler; o catálogo DEVE usar exatamente esta).
    /// </summary>
    public const string PathPattern =
        @"(?!.*\\\.{1,2}(?:\\|$))(?!.*[ .](?:\\|$))(?!.*~[0-9])[A-Za-z]:(?:\\[^\\/:*?""<>|\x00-\x1F]+)+";

    [GeneratedRegex("^(?:" + PathPattern + @")\z", RegexOptions.CultureInvariant, 200)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex DeviceName();

    /// <summary>Pastas de primeiro nível proibidas em QUALQUER unidade.</summary>
    private static readonly HashSet<string> ForbiddenTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "System Volume Information", "$Recycle.Bin", "Recovery",
        "Boot", "EFI", "$WinREAgent", "Windows.old", "$WINDOWS.~BT", "$Windows.~WS", "$SysReset", "$GetCurrent", "PerfLogs",
        "Config.Msi", "Documents and Settings", "MSOCache", "OneDriveTemp", "$Extend",
    };

    /// <summary>Arquivos do sistema que nunca são alvo, em qualquer lugar.</summary>
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "BOOTSECT.BAK", "DumpStack.log", "DumpStack.log.tmp", "desktop.ini",
    };

    /// <summary>Subpastas do perfil proibidas (chaves de criptografia do usuário, registro do usuário).</summary>
    private static readonly string[][] ForbiddenProfileSubtrees =
    [
        ["AppData", "Local", "Microsoft", "Windows"],
        ["AppData", "Local", "Microsoft", "Credentials"],
        ["AppData", "Roaming", "Microsoft", "Protect"],
        ["AppData", "Roaming", "Microsoft", "Credentials"],
        ["AppData", "Roaming", "Microsoft", "Crypto"],
        ["AppData", "Roaming", "Microsoft", "SystemCertificates"],
        ["AppData", "Roaming", "Microsoft", "Windows", "Start Menu"],
    ];

    private readonly string[] _windowsDirectory;
    private readonly string _systemDrive;
    private readonly List<string[]> _extraForbidden;

    /// <param name="windowsDirectory">Pasta do Windows (ex.: C:\Windows); a unidade dela é a do sistema.</param>
    /// <param name="extraForbidden">Outras raízes proibidas (ex.: a pasta de dados do NEXUS, com o diário).</param>
    public SecureDeletePolicy(string windowsDirectory, IEnumerable<string>? extraForbidden = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsDirectory);
        _windowsDirectory = Split(windowsDirectory);
        _systemDrive = _windowsDirectory[0];
        _extraForbidden = (extraForbidden ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(Split).ToList();
    }

    private static string[] Split(string path) => path.TrimEnd('\\').Split('\\');

    private static bool StartsWith(string[] segments, string[] prefix)
    {
        if (prefix.Length == 0 || segments.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (!string.Equals(segments[i], prefix[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Formato aceito pelo catálogo (o mesmo teste que o broker faz).</summary>
    public static bool MatchesPattern(string path) => path is { Length: > 0 and <= 260 } && PathRegex().IsMatch(path);

    /// <summary>Valida um caminho de arquivo. Retorna null se permitido, ou o motivo da recusa em português simples.</summary>
    public string? Check(string path)
    {
        if (string.IsNullOrEmpty(path)) return "Nenhum arquivo escolhido.";
        if (path.Length > 260) return "Caminho longo demais (máximo de 260 caracteres).";
        if (!MatchesPattern(path))
            return "Caminho fora do formato permitido: use um caminho completo (C:\\...), sem \"..\", sem nomes curtos (~1) e sem caracteres especiais.";

        var s = Split(path);
        var name = s[^1];
        foreach (var seg in s.Skip(1))
            if (DeviceName().IsMatch(seg)) return $"\"{seg}\" é um nome de dispositivo do Windows.";
        if (ForbiddenNames.Contains(name) || name.StartsWith("DumpStack.log", StringComparison.OrdinalIgnoreCase))
            return $"{name} é um arquivo do sistema. O NEXUS nunca o apaga (veja a explicação dele na análise de disco).";
        if (name.StartsWith("ntuser.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("usrclass.dat", StringComparison.OrdinalIgnoreCase))
            return "Arquivo do registro do usuário: apagar corrompe o perfil.";

        if (s.Length == 2)
        {
            if (string.Equals(s[0], _systemDrive, StringComparison.OrdinalIgnoreCase))
                return "Arquivos na raiz do disco do sistema não são aceitos.";
        }
        else if (ForbiddenTopLevel.Contains(s[1]))
            return $"Arquivos dentro de \"{s[0]}\\{s[1]}\" pertencem ao Windows ou a programas instalados e não são aceitos.";

        if (StartsWith(s, _windowsDirectory))
            return "Arquivos da pasta do Windows não são aceitos.";
        foreach (var extra in _extraForbidden)
            if (StartsWith(s, extra))
                return "Esse arquivo está numa pasta protegida (dados do NEXUS ou do sistema).";

        if (s.Length >= 2 && string.Equals(s[1], "Users", StringComparison.OrdinalIgnoreCase))
        {
            if (s.Length <= 3) return "Arquivos soltos em \"Users\" não são aceitos.";
            if (s[2] is var profile && (profile.Equals("Default", StringComparison.OrdinalIgnoreCase) || profile.Equals("Default User", StringComparison.OrdinalIgnoreCase)
                || profile.Equals("All Users", StringComparison.OrdinalIgnoreCase)))
                return "O perfil padrão do Windows não é aceito.";
            if (s.Length == 4) return "Arquivos na raiz do perfil do usuário (onde fica o registro do usuário) não são aceitos.";
            var inProfile = s[3..];
            foreach (var sub in ForbiddenProfileSubtrees)
                if (StartsWith(inProfile, sub))
                    return "Essa pasta do perfil guarda chaves de criptografia, credenciais ou o registro do usuário; apagar algo ali pode trancar você fora dos seus dados.";
        }
        return null;
    }
}
