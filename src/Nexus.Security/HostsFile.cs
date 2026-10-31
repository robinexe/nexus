using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Security;

public sealed record HostsEntry(int LineIndex, string Address, IReadOnlyList<string> Hosts, bool IsBlock);

/// <summary>Achado no arquivo hosts. <see cref="Fixable"/>: a correção reversível do NEXUS desativaria esta linha.</summary>
public sealed record HostsIssue(HostsEntry Entry, string Host, Severity Severity, string Reason, bool Fixable);

/// <summary>
/// Arquivo hosts (<c>%SystemRoot%\System32\drivers\etc\hosts</c>): parser puro, análise e "desativação" (comentar a linha,
/// nunca apagar). Domínios de segurança, atualização e login redirecionados para outro IP são o sinal clássico de
/// sequestro (malware impede o antivírus de atualizar ou leva a páginas falsas). Bloqueios de telemetria são tratados
/// como escolha provavelmente intencional do usuário (privacidade) e não são mexidos.
/// </summary>
public static class HostsFile
{
    public const string DisabledPrefix = "# [NEXUS desativou] ";
    public const int MaxBytes = 64 * 1024 * 1024;

    /// <summary>Domínios cujo redirecionamento indica sequestro (o próprio domínio e subdomínios).</summary>
    public static readonly IReadOnlyList<string> ProtectedDomains =
    [
        "microsoft.com", "windowsupdate.com", "windows.com", "windows.net", "microsoftonline.com", "live.com", "msftncsi.com",
        "msftconnecttest.com", "office.com", "office365.com", "outlook.com", "bing.com", "msn.com", "xbox.com", "azureedge.net",
        "google.com", "googleapis.com", "gstatic.com", "youtube.com", "mozilla.org", "mozilla.net", "apple.com", "github.com",
        "virustotal.com", "avast.com", "avg.com", "kaspersky.com", "kaspersky-labs.com", "eset.com", "mcafee.com", "norton.com",
        "symantec.com", "symantecliveupdate.com", "nortonlifelock.com", "bitdefender.com", "bitdefender.net", "malwarebytes.com",
        "trendmicro.com", "sophos.com", "f-secure.com", "avira.com", "comodo.com", "emsisoft.com", "drweb.com", "pandasecurity.com",
        "paypal.com", "steampowered.com", "steamcommunity.com", "epicgames.com", "riotgames.com", "discord.com",
    ];

    /// <summary>Trechos de nomes de telemetria/relatórios: bloqueá-los é escolha de privacidade, não sequestro.</summary>
    private static readonly string[] TelemetryHints =
        ["telemetry", "vortex", "watson", "settings-win", "events.data", "data.microsoft.com", "diagnostics", "oca.", "sqm.", "feedback", "ads.", "adnxs", "doubleclick"];

    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static IReadOnlyList<HostsEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var list = new List<HostsEntry>();
        var lines = SplitLines(text);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Content;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash];
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !IPAddress.TryParse(tokens[0], out var ip)) continue;
            var hosts = tokens.Skip(1).Select(h => h.Trim().TrimEnd('.').ToLowerInvariant()).Where(h => h.Length > 0).ToList();
            if (hosts.Count == 0) continue;
            list.Add(new HostsEntry(i, tokens[0], hosts, IsBlockAddress(ip)));
        }
        return list;
    }

    public static bool IsBlockAddress(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (IPAddress.IsLoopback(ip)) return true;
        return ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None);
    }

    public static string? ProtectedMatch(string host) =>
        ProtectedDomains.FirstOrDefault(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikeTelemetry(string host) => TelemetryHints.Any(h => host.Contains(h, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<HostsIssue> Analyze(IReadOnlyList<HostsEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var issues = new List<HostsIssue>();
        foreach (var e in entries)
            foreach (var host in e.Hosts)
            {
                if (host is "localhost" or "localhost.localdomain" || host.EndsWith(".localhost", StringComparison.Ordinal)) continue;
                var domain = ProtectedMatch(host);
                if (domain is null) continue;
                if (!e.IsBlock)
                {
                    issues.Add(new(e, host, Severity.High,
                        $"{host} (domínio de {domain}) é redirecionado para {e.Address}. Isso leva o PC a outro servidor: pode ser página falsa ou bloqueio do antivírus.", true));
                }
                else if (LooksLikeTelemetry(host))
                {
                    issues.Add(new(e, host, Severity.Info,
                        $"{host} está bloqueado ({e.Address}). Parece bloqueio de telemetria ou anúncios, geralmente feito de propósito por privacidade.", false));
                }
                else
                {
                    issues.Add(new(e, host, Severity.Medium,
                        $"{host} (domínio de {domain}) está bloqueado ({e.Address}). Isso pode impedir atualizações, login ou o antivírus de funcionar.", true));
                }
            }
        return issues;
    }

    /// <summary>
    /// Conteúdo novo com as linhas corrigíveis comentadas (prefixo <see cref="DisabledPrefix"/>). Nada é apagado; quebras de
    /// linha e demais linhas ficam idênticas. Retorna o texto original se não houver o que desativar.
    /// </summary>
    public static string DisableFixable(string text, out int disabledLines)
    {
        ArgumentNullException.ThrowIfNull(text);
        var fixLines = Analyze(Parse(text)).Where(i => i.Fixable).Select(i => i.Entry.LineIndex).ToHashSet();
        disabledLines = fixLines.Count;
        if (fixLines.Count == 0) return text;
        var sb = new StringBuilder(text.Length + fixLines.Count * DisabledPrefix.Length);
        var lines = SplitLines(text);
        for (var i = 0; i < lines.Count; i++)
        {
            if (fixLines.Contains(i)) sb.Append(DisabledPrefix);
            sb.Append(lines[i].Content).Append(lines[i].Ending);
        }
        return sb.ToString();
    }

    private static List<(string Content, string Ending)> SplitLines(string text)
    {
        var result = new List<(string, string)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                result.Add((text[start..end], text[end..(i + 1)]));
                start = i + 1;
            }
        }
        if (start < text.Length) result.Add((text[start..], ""));
        return result;
    }

    /// <summary>Converte achados do hosts em pontos suspeitos para a página.</summary>
    public static IReadOnlyList<Finding> ToFindings(IReadOnlyList<HostsIssue> issues, int totalEntries, string fixActionId)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var findings = new List<Finding>();
        foreach (var g in issues.GroupBy(i => i.Severity).OrderByDescending(g => g.Key))
        {
            var sample = string.Join(", ", g.Select(i => i.Host).Distinct().Take(6)) + (g.Select(i => i.Host).Distinct().Count() > 6 ? "…" : "");
            var count = g.Select(i => i.Host).Distinct().Count();
            findings.Add(g.Key switch
            {
                Severity.High => new Finding("Arquivo hosts", Severity.High, $"Hosts redireciona {count} domínio(s) importante(s)", sample,
                    "Malware usa o hosts para mandar sites de bancos, login ou antivírus para outro servidor.",
                    @"Abra C:\Windows\System32\drivers\etc\hosts no Bloco de Notas e procure os nomes listados.",
                    "Desativar as linhas (o NEXUS comenta as linhas, guarda cópia do original e pode desfazer).", fixActionId),
                Severity.Medium => new Finding("Arquivo hosts", Severity.Medium, $"Hosts bloqueia {count} domínio(s) de atualização ou segurança", sample,
                    "Bloquear esses domínios impede atualizações, login ou o antivírus. Alguns programas de \"debloat\" fazem isso; malware também.",
                    @"Abra C:\Windows\System32\drivers\etc\hosts e veja se você reconhece essas linhas.",
                    "Se você não colocou essas linhas, desative-as (reversível).", fixActionId),
                _ => new Finding("Arquivo hosts", Severity.Info, $"Hosts bloqueia {count} domínio(s) de telemetria/anúncios", sample,
                    "Bloqueio de telemetria costuma ser intencional (privacidade). Não é sinal de ameaça.",
                    "Confira se foi você ou uma ferramenta de privacidade que adicionou.",
                    "Nada a fazer se foi intencional. O NEXUS não mexe nessas linhas."),
            });
        }
        if (totalEntries > 2000)
            findings.Add(new Finding("Arquivo hosts", Severity.Info, $"Hosts com {totalEntries} entradas", "Lista grande de bloqueio",
                "Listas grandes de bloqueio são comuns (anúncios). Não é ameaça, mas pode atrasar a resolução de nomes em PCs lentos.",
                "Veja se você instalou uma lista de bloqueio.", "Nada a fazer se foi intencional."));
        return findings;
    }
}
