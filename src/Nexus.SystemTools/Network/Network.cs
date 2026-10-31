using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Nexus.Core;

namespace Nexus.SystemTools.Network;

public interface INetworkBackend
{
    /// <summary>Esvazia o cache do cliente DNS (o mesmo que <c>ipconfig /flushdns</c>).</summary>
    void FlushDns();

    /// <summary>Esvazia o cache ARP (<c>netsh interface ip delete arpcache</c>; exige administrador).</summary>
    void FlushArp();
}

/// <summary>Limpar o cache DNS. Efêmero: não há o que restaurar (o Windows refaz o cache sozinho conforme os nomes são usados).</summary>
public sealed class DnsFlushHandler(INetworkBackend backend) : EphemeralHandler
{
    public const string Kind = "SystemDnsFlush";
    public override string HandlerKind => Kind;
    protected override void Run(ActionInstance action) => backend.FlushDns();
}

/// <summary>Limpar o cache ARP. Efêmero: as entradas dinâmicas são reaprendidas em segundos.</summary>
public sealed class ArpFlushHandler(INetworkBackend backend) : EphemeralHandler
{
    public const string Kind = "SystemArpFlush";
    public override string HandlerKind => Kind;
    protected override void Run(ActionInstance action) => backend.FlushArp();
}

/// <summary>Leitura das saídas de <c>ipconfig /displaydns</c> e <c>arp -a</c> (independente do idioma).</summary>
public static partial class NetworkParsers
{
    [GeneratedRegex(@"^\s*-{10,}\s*$")]
    private static partial Regex DashLine();

    [GeneratedRegex(@"^\s*\d{1,3}(\.\d{1,3}){3}\s+([0-9a-fA-F]{2}-){5}[0-9a-fA-F]{2}\s+(\S+)\s*$")]
    private static partial Regex ArpLine();

    /// <summary>Entradas do cache DNS: cada uma é um nome seguido de uma linha de traços (em qualquer idioma).</summary>
    public static int CountDnsEntries(string displayDns)
    {
        ArgumentNullException.ThrowIfNull(displayDns);
        return Text.Lines(displayDns).Count(l => DashLine().IsMatch(l));
    }

    /// <summary>Entradas dinâmicas do cache ARP ("dynamic"/"dinâmico"); estáticas e difusão não são apagadas pela limpeza.</summary>
    public static int CountArpDynamic(string arpOutput)
    {
        ArgumentNullException.ThrowIfNull(arpOutput);
        var n = 0;
        foreach (var line in Text.Lines(arpOutput))
        {
            var m = ArpLine().Match(line);
            if (!m.Success) continue;
            var type = Text.Fold(m.Groups[3].Value);
            if (type.StartsWith("dyn", StringComparison.Ordinal) || type.StartsWith("din", StringComparison.Ordinal)) n++;
        }
        return n;
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsNetworkBackend(IProcessRunner runner) : INetworkBackend
{
    public void FlushDns()
    {
        if (!DnsFlushResolverCache())
            throw new InvalidOperationException($"DnsFlushResolverCache falhou (código {Marshal.GetLastPInvokeError()}). O serviço Cliente DNS está em execução?");
    }

    public void FlushArp()
    {
        var r = runner.Run(SystemPaths.Tool("netsh.exe"), ["interface", "ip", "delete", "arpcache"], TimeSpan.FromSeconds(30));
        if (r.ExitCode != 0) throw new InvalidOperationException($"netsh não limpou o cache ARP (código {r.ExitCode}): {r.Combined.Trim()}");
    }

    /// <summary>Número de entradas no cache DNS, ou null se não deu para ler.</summary>
    public int? CountDns()
    {
        try
        {
            var r = runner.Run(SystemPaths.Tool("ipconfig.exe"), ["/displaydns"], TimeSpan.FromSeconds(20));
            return r.ExitCode == 0 ? NetworkParsers.CountDnsEntries(r.Output) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { return null; }
    }

    public int? CountArp()
    {
        try
        {
            var r = runner.Run(SystemPaths.Tool("arp.exe"), ["-a"], TimeSpan.FromSeconds(20));
            return r.ExitCode == 0 ? NetworkParsers.CountArpDynamic(r.Output) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { return null; }
    }

    [LibraryImport("dnsapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}
