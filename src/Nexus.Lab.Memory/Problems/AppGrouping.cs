namespace Nexus.Lab.Memory.Problems;

public enum AppKind
{
    App,
    /// <summary>Navegador (um processo por aba/extensão/GPU: só faz sentido somado).</summary>
    Browser,
    /// <summary>svchost.exe: hospedeiro de serviços do Windows (agrupado pelos serviços que roda).</summary>
    ServiceHost,
    /// <summary>Processo de serviço próprio (um .exe que é um serviço).</summary>
    Service,
    /// <summary>Máquina virtual / WSL (Vmmem): memória da VM inteira.</summary>
    VirtualMachine,
    /// <summary>Núcleo do Windows (System, Registry, Memory Compression, csrss...).</summary>
    System,
}

/// <summary>
/// Um "app" para o usuário: todos os processos com o mesmo papel somados (Edge com 30 processos = 1 cartão).
/// <see cref="Key"/> é estável entre leituras (svchost reiniciado muda de PID, mas os serviços são os mesmos).
/// </summary>
public sealed record AppGroup(
    string Key,
    string DisplayName,
    AppKind Kind,
    IReadOnlyList<ProcSample> Processes,
    IReadOnlyList<ServiceInfo> Services)
{
    public long PrivateBytes => Processes.Sum(p => p.PrivateBytes);
    public long WorkingSet => Processes.Sum(p => p.WorkingSet);
    public long Handles => Processes.Sum(p => (long)p.Handles);
    public long Threads => Processes.Sum(p => (long)p.Threads);
    /// <summary>Maior contagem de GDI de um processo do grupo (o limite de 10.000 é por processo).</summary>
    public int? MaxGdi => MaxOf(p => p.GdiObjects);
    public int? MaxUser => MaxOf(p => p.UserObjects);
    public int ProcessCount => Processes.Count;

    private int? MaxOf(Func<ProcSample, int?> pick)
    {
        int? max = null;
        foreach (var p in Processes)
            if (pick(p) is { } v && (max is null || v > max)) max = v;
        return max;
    }
}

/// <summary>Agrupa processos por app (navegadores e apps multi-processo somados) e svchost pelos serviços.</summary>
public static class AppGrouping
{
    private sealed record Known(string Key, string Display, AppKind Kind);

    // Nome do executável (sem .exe, minúsculo) → app. Apps de vários processos conhecidos; o resto agrupa pelo próprio nome.
    private static readonly Dictionary<string, Known> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = new("msedge", "Microsoft Edge", AppKind.Browser),
        ["msedgewebview2"] = new("msedgewebview2", "Edge WebView2 (navegador embutido em apps)", AppKind.App),
        ["chrome"] = new("chrome", "Google Chrome", AppKind.Browser),
        ["firefox"] = new("firefox", "Mozilla Firefox", AppKind.Browser),
        ["opera"] = new("opera", "Opera / Opera GX", AppKind.Browser),
        ["brave"] = new("brave", "Brave", AppKind.Browser),
        ["vivaldi"] = new("vivaldi", "Vivaldi", AppKind.Browser),
        ["discord"] = new("discord", "Discord", AppKind.App),
        ["discordptb"] = new("discord", "Discord", AppKind.App),
        ["discordcanary"] = new("discord", "Discord", AppKind.App),
        ["ms-teams"] = new("teams", "Microsoft Teams", AppKind.App),
        ["teams"] = new("teams", "Microsoft Teams", AppKind.App),
        ["code"] = new("code", "Visual Studio Code", AppKind.App),
        ["steam"] = new("steam", "Steam", AppKind.App),
        ["steamwebhelper"] = new("steam", "Steam", AppKind.App),
        ["epicgameslauncher"] = new("epic", "Epic Games Launcher", AppKind.App),
        ["epicwebhelper"] = new("epic", "Epic Games Launcher", AppKind.App),
        ["spotify"] = new("spotify", "Spotify", AppKind.App),
        ["slack"] = new("slack", "Slack", AppKind.App),
        ["whatsapp"] = new("whatsapp", "WhatsApp", AppKind.App),
        ["battle.net"] = new("battlenet", "Battle.net", AppKind.App),
        ["riotclientservices"] = new("riot", "Riot Client", AppKind.App),
        ["riotclientux"] = new("riot", "Riot Client", AppKind.App),
        ["riotclientuxrender"] = new("riot", "Riot Client", AppKind.App),
        ["vmmem"] = new("vmmem", "Máquina virtual / WSL (Vmmem)", AppKind.VirtualMachine),
        ["vmmemwsl"] = new("vmmem", "Máquina virtual / WSL (Vmmem)", AppKind.VirtualMachine),
        ["vmwp"] = new("vmwp", "Hyper-V (processo de VM)", AppKind.VirtualMachine),
        ["memory compression"] = new("memcompression", "Compressão de memória do Windows", AppKind.System),
        ["system"] = new("system", "Sistema (núcleo do Windows)", AppKind.System),
        ["registry"] = new("registry", "Registro do Windows", AppKind.System),
        ["idle"] = new("idle", "Ocioso", AppKind.System),
        ["secure system"] = new("securesystem", "Sistema seguro (VBS)", AppKind.System),
        ["csrss"] = new("csrss", "csrss (núcleo do Windows)", AppKind.System),
        ["smss"] = new("smss", "smss (núcleo do Windows)", AppKind.System),
        ["wininit"] = new("wininit", "wininit (núcleo do Windows)", AppKind.System),
        ["winlogon"] = new("winlogon", "winlogon (núcleo do Windows)", AppKind.System),
        ["services"] = new("services", "Gerenciador de serviços", AppKind.System),
        ["lsass"] = new("lsass", "lsass (segurança do Windows)", AppKind.System),
        ["dwm"] = new("dwm", "Gerenciador de janelas da área de trabalho (dwm)", AppKind.System),
        ["fontdrvhost"] = new("fontdrvhost", "Host de fontes", AppKind.System),
    };

    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    public static bool IsBrowser(string processName) =>
        KnownApps.TryGetValue(NormalizeName(processName), out var k) && k.Kind == AppKind.Browser;

    /// <summary>Agrupa uma leitura. Ordem: maior memória privada primeiro.</summary>
    public static IReadOnlyList<AppGroup> Group(IReadOnlyList<ProcSample> processes, IReadOnlyDictionary<int, IReadOnlyList<ServiceInfo>>? servicesByPid = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        servicesByPid ??= new Dictionary<int, IReadOnlyList<ServiceInfo>>();
        var buckets = new Dictionary<string, (string Display, AppKind Kind, List<ProcSample> Procs, List<ServiceInfo> Services)>(StringComparer.Ordinal);
        foreach (var p in processes)
        {
            var name = NormalizeName(p.Name);
            servicesByPid.TryGetValue(p.Pid, out var services);
            services ??= [];
            string key, display;
            AppKind kind;
            if (name.Equals("svchost", StringComparison.OrdinalIgnoreCase))
            {
                kind = AppKind.ServiceHost;
                if (services.Count > 0)
                {
                    var names = services.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
                    key = "svc:" + string.Join(",", names).ToLowerInvariant();
                    display = services.Count == 1
                        ? $"Serviço: {services[0].DisplayName}"
                        : $"Serviços: {string.Join(", ", services.Take(3).Select(s => s.DisplayName))}{(services.Count > 3 ? $" e mais {services.Count - 3}" : "")}";
                }
                else
                {
                    key = $"svc:pid:{p.Pid}";
                    display = "Host de serviços (svchost) sem serviço identificado";
                }
            }
            else if (KnownApps.TryGetValue(name, out var known))
            {
                key = "app:" + known.Key;
                display = known.Display;
                kind = known.Kind;
            }
            else
            {
                key = "app:" + name.ToLowerInvariant();
                kind = services.Count > 0 ? AppKind.Service : AppKind.App;
                display = services.Count > 0 ? $"{name} (serviço {services[0].DisplayName})" : name;
            }
            if (!buckets.TryGetValue(key, out var b)) { b = (display, kind, [], []); buckets[key] = b; }
            b.Procs.Add(p);
            foreach (var s in services)
                if (!b.Services.Any(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))) b.Services.Add(s);
        }
        return buckets
            .Select(kv => new AppGroup(kv.Key, kv.Value.Display, kv.Value.Kind, kv.Value.Procs, kv.Value.Services))
            .OrderByDescending(g => g.PrivateBytes)
            .ToList();
    }
}
