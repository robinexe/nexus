using System.Text.RegularExpressions;

namespace Nexus.Boot.Inventory;

/// <summary>Extrai o executável de uma linha de comando de inicialização (Run, tarefa, serviço). Lógica pura.</summary>
public static partial class CommandLine
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat", ".cmd", ".vbs", ".js", ".ps1", ".lnk", ".scr"];

    [GeneratedRegex(@"%([A-Za-z0-9_()]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex EnvVar();

    /// <summary>Caminho do executável (ainda com variáveis de ambiente) ou null se a linha estiver vazia.</summary>
    public static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            var quoted = end < 0 ? c[1..] : c[1..end];
            return quoted.Length == 0 ? null : quoted;
        }
        // Sem aspas: o primeiro trecho que termina numa extensão executável (caminhos com espaço sem aspas são comuns em Run).
        var best = -1;
        foreach (var ext in ExecutableExtensions)
        {
            var i = c.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                var after = i + ext.Length;
                if (after == c.Length || c[after] is ' ' or '\t' or ',')
                {
                    if (best < 0 || after < best) best = after;
                    break;
                }
                i = c.IndexOf(ext, after, StringComparison.OrdinalIgnoreCase);
            }
        }
        if (best > 0) return c[..best];
        var space = c.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? c : c[..space];
    }

    /// <summary>"\??\C:\x" e "\SystemRoot\x" (caminhos de serviço) para a forma Win32.</summary>
    public static string NormalizeServicePath(string path, string windowsDirectory)
    {
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) return path[4..];
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return windowsDirectory.TrimEnd('\\') + path[11..];
        if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) return windowsDirectory.TrimEnd('\\') + "\\" + path;
        return path;
    }

    /// <summary>
    /// Expande %VAR% com <paramref name="lookup"/>. Retorna null se sobrar variável sem valor conhecido: nesse caso não dá para
    /// afirmar se o arquivo existe (e nada deve ser removido por "não existir").
    /// </summary>
    public static string? Expand(string path, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var unresolved = false;
        var result = EnvVar().Replace(path, m =>
        {
            var v = lookup(m.Groups[1].Value);
            if (string.IsNullOrEmpty(v)) { unresolved = true; return m.Value; }
            return v;
        });
        return unresolved ? null : result;
    }

    /// <summary>Caminho absoluto de Windows ("C:\..." ou "\\servidor\...").</summary>
    public static bool IsAbsoluteWindowsPath(string? path) =>
        path is { Length: >= 3 } && ((char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\') || path.StartsWith(@"\\", StringComparison.Ordinal));

    public static string FileNameOf(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i < 0 ? path : path[(i + 1)..];
    }
}
