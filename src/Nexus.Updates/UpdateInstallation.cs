using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

namespace Nexus.Updates;

/// <summary>Como esta cópia do NEXUS foi obtida (decide se atualiza sozinha).</summary>
public enum InstallKind
{
    /// <summary>Compilada localmente (sem a marca da publicação): nada de atualização automática.</summary>
    Development,
    /// <summary>Instalada pelo instalador (Arquivos de Programas, desinstalador registrado): baixa e instala sozinha.</summary>
    Installed,
    /// <summary>ZIP portátil: só avisa, com link (o instalador iria para outra pasta).</summary>
    Portable,
}

/// <summary>O que fazer com uma versão nova encontrada.</summary>
public enum UpdateMode
{
    /// <summary>Versão de desenvolvimento: não procura sozinha nem instala.</summary>
    Disabled,
    /// <summary>Baixa, confere e instala (com o aviso do Windows).</summary>
    Install,
    /// <summary>Só avisa, com link para a página de downloads (portátil, ou versão antiga demais para pular direto).</summary>
    NotifyOnly,
}

/// <summary>Versão e tipo de cópia do NEXUS em execução.</summary>
public sealed record AppVersionInfo(SemVersion Version, string Display, bool Packaged)
{
    /// <summary>Metadado gravado só pela publicação (tools/packaging/publish.py passa -p:NexusPackaged=true).</summary>
    public const string PackagedMetadata = "NexusPackaged";

    /// <summary>Lê AssemblyInformationalVersion (tira o "+hash") e a marca de publicação do assembly.</summary>
    public static AppVersionInfo FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var packaged = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == PackagedMetadata && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));
        return From(informational, assembly.GetName().Version, packaged);
    }

    public static AppVersionInfo From(string? informational, Version? fallback, bool packaged)
    {
        var text = informational ?? "";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) text = text[..plus];
        if (!SemVersion.TryParse(text, out var v))
        {
            var f = fallback ?? new Version(0, 0, 0);
            v = SemVersion.Parse($"{Math.Max(0, f.Major)}.{Math.Max(0, f.Minor)}.{Math.Max(0, f.Build)}");
        }
        return new AppVersionInfo(v, v.ToString(), packaged);
    }
}

/// <summary>Detecção do tipo de cópia e montagem do comando do instalador (lógica pura) + partes do Windows.</summary>
public static class UpdateInstallation
{
    /// <summary>Chave do desinstalador do Inno Setup (AppId do nexus.iss + "_is1").</summary>
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{16A95420-0C76-4948-AB9E-781ED827C4EE}_is1";

    /// <summary>Argumentos fixos do instalador silencioso. /RELAUNCH=1 faz o [Run] do nexus.iss reabrir o NEXUS como o usuário original.</summary>
    public static IReadOnlyList<string> SilentArguments { get; } = ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS"];

    /// <summary>
    /// Instalada = o desinstalador registrado aponta para esta pasta; ou, sem registro legível, desinstalador do Inno ao lado
    /// numa pasta dentro de Arquivos de Programas. Qualquer outra pasta = portátil. Sem a marca da publicação = desenvolvimento.
    /// </summary>
    public static InstallKind Classify(bool packaged, string baseDirectory, string? registeredInstallLocation, IEnumerable<string> programFilesRoots,
        bool hasUninstaller)
    {
        if (!packaged) return InstallKind.Development;
        var self = Normalize(baseDirectory);
        if (registeredInstallLocation is { Length: > 0 } reg) return Normalize(reg) == self ? InstallKind.Installed : InstallKind.Portable;
        var underProgramFiles = programFilesRoots.Where(r => !string.IsNullOrEmpty(r)).Any(r => self.StartsWith(Normalize(r), StringComparison.Ordinal));
        return hasUninstaller && underProgramFiles ? InstallKind.Installed : InstallKind.Portable;
    }

    /// <summary>Instala sozinho só a cópia instalada e só se o manifesto não exigir uma versão mínima maior que esta.</summary>
    public static UpdateMode ModeFor(InstallKind kind, UpdateManifest manifest, SemVersion current)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return kind switch
        {
            InstallKind.Development => UpdateMode.Disabled,
            InstallKind.Portable => UpdateMode.NotifyOnly,
            _ when manifest.MinimumVersion is { } min && current < min => UpdateMode.NotifyOnly,
            _ => UpdateMode.Install,
        };
    }

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant() + "\\";

    /// <summary>
    /// Linha de comando para iniciar o instalador. Com <paramref name="relaunch"/>, vai por cmd.exe: <c>start /wait</c> espera o
    /// instalador; se ele falhar ou o aviso do Windows for recusado (código ≠ 0), o NEXUS é reaberto com --update-failed (no
    /// sucesso quem reabre é o próprio instalador, /RELAUNCH=1). Sem cmd quando algum caminho tem caractere especial do cmd.
    /// </summary>
    public static (string FileName, string Arguments) BuildCommand(string installer, string? logPath, bool relaunch, string? appExe, string cmdExe)
    {
        var args = new List<string>(SilentArguments);
        if (relaunch) args.Add("/RELAUNCH=1");
        if (!string.IsNullOrEmpty(logPath)) args.Add($"/LOG=\"{logPath}\"");
        var installerArgs = string.Join(' ', args);
        if (!relaunch || appExe is null || !CmdSafe(installer) || !CmdSafe(appExe) || (logPath is not null && !CmdSafe(logPath)))
            return (installer, installerArgs);
        var line = new StringBuilder();
        line.Append("/d /s /c \"start \"\" /wait \"").Append(installer).Append("\" ").Append(installerArgs)
            .Append(" & if errorlevel 1 start \"\" \"").Append(appExe).Append("\" --update-failed\"");
        return (cmdExe, line.ToString());
    }

    /// <summary>Caminho sem os caracteres que o cmd interpretaria (%, ^, &amp;, |, &lt;, &gt;, aspas, !).</summary>
    public static bool CmdSafe(string path) =>
        !string.IsNullOrEmpty(path) && path.IndexOfAny(['%', '^', '&', '|', '<', '>', '"', '!', '\r', '\n']) < 0;

    // ------------------------------------------------------------------ Windows

    [SupportedOSPlatform("windows")]
    public static InstallKind Detect(bool packaged, string baseDirectory)
    {
        string? registered = null;
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string s && s.Length > 0) { registered = s; break; }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        return Classify(packaged, baseDirectory, registered, roots, File.Exists(Path.Combine(baseDirectory, "unins000.exe")));
    }

    /// <summary>
    /// Inicia o instalador conferido (o Windows pede permissão de administrador uma vez). Confere de novo tamanho e SHA-256
    /// imediatamente antes. Sem verbo "runas": o próprio instalador se eleva e mantém a parte sem elevação, que reabre o NEXUS
    /// como o usuário original (runasoriginaluser no nexus.iss).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Start(string installer, UpdateInstaller expected, bool relaunch, string? appExe, string? logPath)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!UpdateClient.Matches(installer, expected)) throw new UpdateException("O instalador baixado mudou ou sumiu: não foi executado.");
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var (file, args) = BuildCommand(installer, logPath, relaunch, appExe, cmd);
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = file != cmd,
            CreateNoWindow = file == cmd,
            WorkingDirectory = Path.GetDirectoryName(installer)!,
        };
        using var _ = Process.Start(psi) ?? throw new UpdateException("Não foi possível iniciar o instalador.");
    }
}
