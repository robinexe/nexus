using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Nexus.Programs.Uninstall;

/// <summary>Desinstalador oficial separado em executável + argumentos (nunca uma linha para um interpretador).</summary>
public sealed record UninstallCommand(string FileName, string Arguments, bool IsMsi, string? ProductCode)
{
    public string Display => Arguments.Length == 0 ? $"\"{FileName}\"" : $"\"{FileName}\" {Arguments}";
}

/// <summary>
/// Interpreta o <c>UninstallString</c> do registro. Formatos reais tratados:
/// <list type="bullet">
/// <item><c>MsiExec.exe /I{GUID}</c> ou <c>/X{GUID}</c> → <c>%SystemRoot%\System32\msiexec.exe /x {GUID}</c> (sempre remover, nunca "reparar");</item>
/// <item>caminho entre aspas com ou sem argumentos (<c>"C:\Program Files\App\uninst.exe" /S</c>);</item>
/// <item>caminho SEM aspas com espaços (<c>C:\Program Files\App\unins000.exe /SILENT</c>): escolhe o primeiro prefixo terminado
/// em <c>.exe</c> que existe, e nunca um prefixo sem extensão (a falha clássica de "caminho sem aspas");</item>
/// <item>variáveis de ambiente (<c>%ProgramFiles%</c>), caminhos curtos 8.3 e <c>rundll32.exe arquivo.dll,Função</c>.</item>
/// </list>
/// Recusa interpretadores (cmd, PowerShell, wscript, mshta...), executáveis que não são <c>.exe</c>, caminhos relativos
/// (exceto msiexec/rundll32, resolvidos na pasta do sistema) e arquivos que não existem.
/// </summary>
public static partial class UninstallCommandParser
{
    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    [GeneratedRegex(@"^""?(?:[A-Za-z]:\\(?:[^""]*\\)?)?msiexec(?:\.exe)?""?(?=\s|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MsiExecPrefix();

    private static readonly string[] Interpreters =
        ["cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "bash.exe", "wsl.exe", "conhost.exe", "explorer.exe"];

    /// <param name="raw">Texto do registro.</param>
    /// <param name="fileExists">Existência de arquivo (o real no Windows; falso nos testes).</param>
    /// <param name="expand">Expande variáveis de ambiente.</param>
    /// <param name="systemDirectory">Pasta do sistema (System32) para msiexec/rundll32.</param>
    public static UninstallCommand? Parse(string? raw, Func<string, bool> fileExists, Func<string, string> expand, string systemDirectory, out string? error)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(expand);
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) { error = "O programa não registrou um desinstalador."; return null; }
        var text = expand(raw.Trim()).Trim();
        if (text.Length > 2048) { error = "Comando de desinstalação longo demais."; return null; }
        if (text.IndexOfAny(['\r', '\n', '\0']) >= 0) { error = "Comando de desinstalação com quebra de linha: recusado."; return null; }

        // Windows Installer: sempre msiexec da pasta do sistema, com o código do produto validado.
        if (MsiExecPrefix().Match(text) is { Success: true } msi)
        {
            var rest = text[msi.Length..];
            var guid = Guid().Match(rest);
            if (!guid.Success) { error = "Desinstalador MSI sem código de produto válido."; return null; }
            var code = guid.Value.ToUpperInvariant();
            return Validate(new UninstallCommand(Path.Combine(systemDirectory, "msiexec.exe"), "/x " + code, true, code), fileExists, out error);
        }

        string file, args;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close < 0) { error = "Aspas sem fechamento no comando de desinstalação."; return null; }
            file = text[1..close].Trim();
            args = text[(close + 1)..].Trim();
        }
        else
        {
            (file, args) = SplitUnquoted(text, fileExists);
        }

        if (!Path.IsPathFullyQualified(file))
        {
            var bare = Path.GetFileName(file);
            if (string.Equals(Path.GetFileNameWithoutExtension(bare), "rundll32", StringComparison.OrdinalIgnoreCase) &&
                (Path.GetExtension(bare).Length == 0 || string.Equals(Path.GetExtension(bare), ".exe", StringComparison.OrdinalIgnoreCase)) &&
                file.Equals(bare, StringComparison.Ordinal))
                file = Path.Combine(systemDirectory, "rundll32.exe");
            else { error = $"O desinstalador não tem caminho completo ({file})."; return null; }
        }
        return Validate(new UninstallCommand(file, args, false, null), fileExists, out error);
    }

    /// <summary>Caminho sem aspas: o primeiro prefixo terminado em .exe que existe; senão o primeiro terminado em .exe.</summary>
    internal static (string File, string Args) SplitUnquoted(string text, Func<string, bool> fileExists)
    {
        var cuts = new List<int>();
        for (var i = 0; i < text.Length; i++)
            if (text[i] == ' ') cuts.Add(i);
        cuts.Add(text.Length);

        int? firstExe = null;
        foreach (var cut in cuts)
        {
            var candidate = text[..cut].TrimEnd();
            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            firstExe ??= cut;
            if (fileExists(candidate)) return (candidate, text[cut..].Trim());
        }
        if (firstExe is { } f) return (text[..f].TrimEnd(), text[f..].Trim());
        // Sem ".exe" (ex.: "rundll32 x.dll,Func"): primeiro token.
        var sp = text.IndexOf(' ', StringComparison.Ordinal);
        return sp < 0 ? (text, "") : (text[..sp], text[(sp + 1)..].Trim());
    }

    private static UninstallCommand? Validate(UninstallCommand cmd, Func<string, bool> fileExists, out string? error)
    {
        error = null;
        var name = Path.GetFileName(cmd.FileName);
        if (Interpreters.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            error = $"O desinstalador registrado abre um interpretador de comandos ({name}). Por segurança o NEXUS não o executa: use Configurações > Aplicativos.";
            return null;
        }
        if (!string.Equals(Path.GetExtension(cmd.FileName), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = $"O desinstalador registrado não é um programa .exe ({name}).";
            return null;
        }
        if (!fileExists(cmd.FileName))
        {
            error = $"O desinstalador não existe mais em {cmd.FileName}. O programa pode já ter sido removido: procure as sobras.";
            return null;
        }
        return cmd;
    }
}

/// <summary>
/// Abre o desinstalador OFICIAL do fornecedor, visível (exceção controlada: quem remove é ele, e o usuário vê e controla
/// a janela). Com <c>UseShellExecute</c> o Windows mostra o aviso de administrador quando o desinstalador pede. Executável e
/// argumentos vão separados: nunca passa por <c>cmd /c</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UninstallLauncher
{
    public static UninstallCommand? Prepare(string? uninstallString, out string? error) =>
        UninstallCommandParser.Parse(uninstallString, File.Exists, Environment.ExpandEnvironmentVariables, Environment.SystemDirectory, out error);

    public static Process? Start(UninstallCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!File.Exists(command.FileName)) throw new FileNotFoundException("Desinstalador não encontrado.", command.FileName);
        var psi = new ProcessStartInfo(command.FileName)
        {
            Arguments = command.Arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(command.FileName) ?? Environment.SystemDirectory,
        };
        return Process.Start(psi);
    }

    /// <summary>Abre "Aplicativos instalados" do Windows (para apps da Store e desinstaladores que o NEXUS recusa).</summary>
    public static void OpenWindowsAppsSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true })?.Dispose();
}
