using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Security;

/// <summary>Sinal encontrado numa linha de comando.</summary>
public sealed record CommandSignal(Severity Severity, string Reason, string? Decoded = null);

/// <summary>Onde fica um executável, do ponto de vista de risco.</summary>
public enum PathZone { Unknown, System, ProgramFiles, UserAppData, Temp, Downloads, Public, ProgramDataRoot, Other }

/// <summary>
/// Heurísticas puras sobre linhas de comando (Run, tarefas agendadas, IFEO). Só apontam; nunca removem nada.
/// Fontes dos padrões: MITRE ATT&amp;CK T1059.001 (PowerShell -EncodedCommand / -WindowStyle Hidden), T1218.005 (mshta),
/// T1218.011 (rundll32), T1218.010 (regsvr32 /i:URL), T1105 (certutil/bitsadmin baixando arquivos).
/// </summary>
public static partial class CommandHeuristics
{
    [GeneratedRegex(@"(?:^|\s)[-/](?:e|ec|en|enc|enco|encod|encode|encoded|encodedc|encodedco|encodedcom|encodedcomm|encodedcomma|encodedcomman|encodedcommand)\s+([A-Za-z0-9+/=]{16,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EncodedPattern();

    [GeneratedRegex(@"(?:^|\s)[-/]w(?:i|in|ind|indo|indow|indows|indowst|indowsty|indowstyl|indowstyle)?\s+(?:h|hi|hid|hidd|hidde|hidden|1)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiddenPattern();

    [GeneratedRegex(@"https?://|\\\\[A-Za-z0-9.-]+\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b(?:iex|invoke-expression|downloadstring|downloadfile|frombase64string|invoke-webrequest|iwr|net\.webclient|start-bitstransfer)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PsDownloadPattern();

    private static readonly string[] ScriptHosts = ["powershell.exe", "powershell", "pwsh.exe", "pwsh"];

    /// <summary>Nome do executável (sem pasta) da linha de comando, minúsculo.</summary>
    public static string ExecutableName(string command)
    {
        var path = ExecutablePath(command);
        if (path is null) return "";
        var i = path.LastIndexOfAny(['\\', '/']);
        return (i >= 0 ? path[(i + 1)..] : path).ToLowerInvariant();
    }

    /// <summary>
    /// Caminho do executável: entre aspas, ou o maior prefixo terminado em .exe/.com/.bat/.cmd/.ps1/.vbs/.js/.scr,
    /// ou o primeiro token. Variáveis de ambiente não são expandidas aqui.
    /// </summary>
    public static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : c.Trim('"');
        }
        var m = Regex.Match(c, @"^(.+?\.(?:exe|com|bat|cmd|ps1|vbs|vbe|js|jse|wsf|scr|pif|hta|lnk))(?=\s|$|,)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (m.Success) return m.Groups[1].Value;
        var space = c.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? c[..space] : c;
    }

    /// <summary>Classifica a pasta de um caminho já expandido.</summary>
    public static PathZone Zone(string? path, string? windowsDir = @"C:\Windows")
    {
        if (string.IsNullOrWhiteSpace(path)) return PathZone.Unknown;
        var p = path.Replace('/', '\\');
        bool Has(string part) => p.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (Has(@"\AppData\Local\Temp\") || Has(@"\Windows\Temp\") || Has(@"\Temp\") && Has(@"\AppData\")) return PathZone.Temp;
        if (Has(@"\Downloads\")) return PathZone.Downloads;
        if (Has(@"\Users\Public\")) return PathZone.Public;
        if (Has(@"\AppData\")) return PathZone.UserAppData;
        if (windowsDir is not null && p.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return PathZone.System;
        if (Has(@"\Program Files\") || Has(@"\Program Files (x86)\")) return PathZone.ProgramFiles;
        if (Regex.IsMatch(p, @"^[A-Za-z]:\\ProgramData\\[^\\]+$", RegexOptions.None, TimeSpan.FromMilliseconds(100))) return PathZone.ProgramDataRoot;
        return p.Length > 2 && p[1] == ':' ? PathZone.Other : PathZone.Unknown;
    }

    /// <summary>Sinais de comando ofuscado ou que baixa e executa algo. Lista vazia = nada a apontar.</summary>
    public static IReadOnlyList<CommandSignal> Analyze(string? command)
    {
        var signals = new List<CommandSignal>();
        if (string.IsNullOrWhiteSpace(command)) return signals;
        var exe = ExecutableName(command);
        var lower = command.ToLowerInvariant();
        var isPs = ScriptHosts.Contains(exe) || lower.Contains("powershell", StringComparison.Ordinal) || lower.Contains("pwsh", StringComparison.Ordinal);

        if (isPs)
        {
            var enc = EncodedPattern().Match(command);
            if (enc.Success)
                signals.Add(new(Severity.High, "PowerShell com comando codificado (-EncodedCommand): o texto real fica escondido em Base64.", TryDecodeUtf16(enc.Groups[1].Value)));
            if (HiddenPattern().IsMatch(command))
                signals.Add(new(Severity.Medium, "PowerShell em janela oculta (-WindowStyle Hidden). Alguns programas legítimos usam; malware também."));
            if (PsDownloadPattern().IsMatch(command))
                signals.Add(new(Severity.High, "PowerShell que baixa ou executa texto dinamicamente (IEX/DownloadString/Base64)."));
        }

        if (exe is "mshta.exe" or "mshta" && (UrlPattern().IsMatch(command) || lower.Contains("javascript:", StringComparison.Ordinal) || lower.Contains("vbscript:", StringComparison.Ordinal)))
            signals.Add(new(Severity.High, "mshta executando script ou endereço da internet: técnica comum de malware."));
        if (exe is "rundll32.exe" or "rundll32" && (UrlPattern().IsMatch(command) || lower.Contains("javascript:", StringComparison.Ordinal)))
            signals.Add(new(Severity.High, "rundll32 com endereço da internet ou script: uso fora do normal."));
        if (exe is "regsvr32.exe" or "regsvr32" && lower.Contains("/i:", StringComparison.Ordinal) && (UrlPattern().IsMatch(command) || lower.Contains("scrobj", StringComparison.Ordinal)))
            signals.Add(new(Severity.High, "regsvr32 carregando script remoto (/i:URL scrobj.dll)."));
        if (exe is "certutil.exe" or "certutil" && (lower.Contains("-urlcache", StringComparison.Ordinal) || lower.Contains("-decode", StringComparison.Ordinal)))
            signals.Add(new(Severity.High, "certutil baixando ou decodificando arquivo: uso típico de malware, não de manutenção."));
        if (exe is "bitsadmin.exe" or "bitsadmin" && lower.Contains("/transfer", StringComparison.Ordinal))
            signals.Add(new(Severity.Medium, "bitsadmin baixando arquivo em segundo plano."));
        if (exe is "wscript.exe" or "cscript.exe" or "wscript" or "cscript")
        {
            var zone = Zone(ScriptArgument(command));
            if (zone is PathZone.Temp or PathZone.UserAppData or PathZone.Downloads or PathZone.Public)
                signals.Add(new(Severity.High, "Script (.js/.vbs) rodando de pasta do usuário ou temporária."));
        }
        if (command.Count(ch => ch == '^') >= 4 && exe is "cmd.exe" or "cmd")
            signals.Add(new(Severity.Medium, "cmd com muitos ^ (técnica para esconder palavras do antivírus)."));
        return signals;
    }

    private static string? ScriptArgument(string command)
    {
        var m = Regex.Match(command, @"[""']?([A-Za-z]:\\[^""']+?\.(?:js|jse|vbs|vbe|wsf))", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Decodifica Base64 UTF-16LE (formato do -EncodedCommand) para mostrar ao usuário. Nunca executa.</summary>
    public static string? TryDecodeUtf16(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var text = Encoding.Unicode.GetString(bytes);
            return Text.Clip(new string(text.Where(ch => !char.IsControl(ch) || ch == ' ').ToArray()), 400);
        }
        catch (FormatException) { return null; }
    }
}
