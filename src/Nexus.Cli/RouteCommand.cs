using System.Globalization;
using System.Net;
using System.Text.Json;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.NetRoute;
using Nexus.NetRoute.Analysis;
using Nexus.NetRoute.Discovery;
using Nexus.NetRoute.Geo;
using Nexus.NetRoute.Handlers;
using Nexus.NetRoute.Ip;
using Nexus.NetRoute.Probe;
using Nexus.NetRoute.Profiles;
using Nexus.NetRoute.Relay;

namespace Nexus.Cli;

/// <summary>
/// <c>nexus rota</c>: rota de jogo (docs/modules/netroute.md). Diagnóstico e teste só leem e medem; ligar/desligar o túnel,
/// criar a chave e cadastrar relay vão pelo aplicador de administrador (confirmação, diário, desfazer).
/// </summary>
internal static class RouteCommand
{
    public const string Usage =
        "nexus rota [status]\n" +
        "  Estado: cidade, cliente WireGuard, chave do PC, relays cadastrados e túnel.\n" +
        "nexus rota cidade <cidade>\n" +
        "  Sua cidade (régua física do diagnóstico). Ex.: nexus rota cidade fortaleza\n" +
        "nexus rota diagnosticar [--jogo valorant|cs2|fortnite|generic] [--alvo <IP>] [--segundos 20] [--json]\n" +
        "  Acha o servidor da partida (jogue enquanto roda), mede, faz o traceroute e diz se há excesso que um relay removeria.\n" +
        "  Os servidores vistos ficam guardados para o túnel do jogo (blocos aprendidos).\n" +
        "nexus rota relay codigo\n" +
        "  Cria a chave deste PC (se faltar) e mostra o código para autorizar no relay.\n" +
        "nexus rota relay adicionar <link>\n" +
        "  Instala o WireGuard oficial (se faltar) e cadastra o relay pelo link nexus-relay://v1/... que o relay imprimiu.\n" +
        "nexus rota testar [--relay <id>] [--alvo <IP>] [--jogo <id>]\n" +
        "  Teste A/B: rota direta x relay, em rodadas alternadas. Liga o túnel só em modo medição e desliga no fim.\n" +
        "nexus rota ligar [--jogo <id>] [--relay <id>] [--sem-teste]\n" +
        "  Liga o túnel com os blocos do jogo (ligue ANTES da partida). Por padrão, só liga se o teste A/B mostrar ganho.\n" +
        "nexus rota desligar\n" +
        "  Desliga o túnel (desfaz a execução que o ligou).\n" +
        "nexus rota vigiar [--desligar-sozinho]\n" +
        "  Durante a partida: mede o relay pelo túnel a cada 10 s e avisa se ele piorar (perda ou +30 ms por 3 janelas seguidas).\n" +
        "  Com --desligar-sozinho, desliga o túnel nesse caso (a próxima partida volta à rota direta). Ctrl+C para parar.";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return CliExit.Ok; }
        var sub = args.Count == 0 ? "status" : args[0];
        var opts = Options(args.Skip(1).ToList(), out var positional);
        try
        {
            return sub switch
            {
                "status" => Status(),
                "cidade" => City(positional),
                "diagnosticar" => await DiagnoseAsync(opts).ConfigureAwait(false),
                "relay" when positional.Count >= 1 && positional[0] == "codigo" => Code(opts),
                "relay" when positional.Count >= 2 && positional[0] == "adicionar" => AddRelay(positional[1], opts),
                "testar" => await TestAsync(opts).ConfigureAwait(false),
                "ligar" => await OnAsync(opts).ConfigureAwait(false),
                "desligar" => Off(opts),
                "vigiar" => await WatchAsync(opts).ConfigureAwait(false),
                _ => UsageError(),
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return CliExit.BrokerRefused;
        }
    }

    private static int UsageError()
    {
        Console.Error.WriteLine("Uso inválido.\n" + Usage);
        return CliExit.Usage;
    }

    private static Dictionary<string, string> Options(List<string> args, out List<string> positional)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        positional = [];
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && a is not ("--json" or "--yes" or "--sem-teste")) o[a] = args[++i];
                else o[a] = "true";
            }
            else positional.Add(a);
        }
        return o;
    }

    private static FileNetRouteStore Store => new(FileNetRouteStore.DefaultDirectory);

    // ------------------------------------------------------------------ status e cidade

    private static int Status()
    {
        var s = NetRouteSettings.Load(NetRouteSettings.DefaultPath);
        Console.WriteLine($"Cidade: {Places.Find(s.City)?.Name ?? "não definida (nexus rota cidade <cidade>)"}");
        if (OperatingSystem.IsWindows())
        {
            var exe = NetRoute.Windows.WindowsNetRouteSystem.WireGuardExe;
            Console.WriteLine($"Cliente WireGuard oficial: {(File.Exists(exe) ? (NetRoute.Windows.WindowsNetRouteSystem.SignedByWireGuard(exe) ? "instalado" : "presente, mas a assinatura NÃO confere") : "não instalado")}");
        }
        var pub = Store.ReadPublicKey();
        Console.WriteLine($"Chave do PC: {pub ?? "não criada (nexus rota relay codigo)"}");
        var relays = Store.ReadRelays();
        if (relays.Count == 0) Console.WriteLine("Relays: nenhum (nexus rota relay adicionar <link>)");
        foreach (var r in relays)
            Console.WriteLine($"Relay {r.Id}: {r.Name} — {r.Endpoint}{(Places.Find(r.Place) is { } p ? $" ({p.Name})" : "")}{(pub is not null && !r.AuthorizedFor(pub) ? " — gerado para OUTRA chave" : "")}");
        Console.WriteLine(s.TunnelRunId is { } run
            ? $"Túnel: ligado para {s.TunnelGame} desde {s.TunnelSince:g} (execução {run})"
            : "Túnel: desligado");
        return CliExit.Ok;
    }

    private static int City(List<string> positional)
    {
        var place = Places.Find(string.Join(' ', positional));
        if (place is null)
        {
            Console.Error.WriteLine("Cidade não encontrada. Opções: " + string.Join(", ", Places.All.Select(p => p.Name)));
            return CliExit.NotFound;
        }
        var s = NetRouteSettings.Load(NetRouteSettings.DefaultPath) with { City = place.Id };
        s.Save(NetRouteSettings.DefaultPath);
        Console.WriteLine($"Cidade: {place.Name}.");
        return CliExit.Ok;
    }

    // ------------------------------------------------------------------ diagnóstico

    private static async Task<int> DiagnoseAsync(Dictionary<string, string> o)
    {
        var settings = NetRouteSettings.Load(NetRouteSettings.DefaultPath);
        var user = Places.Find(settings.City);
        var engine = new NetRouteEngine(new IcmpPathProber());
        IPAddress? server = null;
        GameNetProfile? profile = GameNetProfiles.Find(o.GetValueOrDefault("--jogo"));
        var gameId = profile?.Id;

        if (o.TryGetValue("--alvo", out var alvo))
        {
            if (!IPAddress.TryParse(alvo, out server) || !PublicAddress.IsPublic(server)) { Console.Error.WriteLine("--alvo precisa ser um IP público."); return CliExit.Usage; }
        }
        else
        {
            if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("A descoberta do servidor usa o ETW do Windows. Use --alvo <IP> fora do Windows."); return CliExit.Unsupported; }
            var game = NetRoute.Windows.GameProcess.Find(profile?.Id);
            if (game is null) { Console.Error.WriteLine("Nenhum jogo encontrado. Abra o jogo, entre numa partida e rode de novo (ou use --alvo <IP>)."); return CliExit.NotFound; }
            profile = game.Profile;
            gameId = game.GameId;
            var seconds = int.TryParse(o.GetValueOrDefault("--segundos"), NumberStyles.None, CultureInfo.InvariantCulture, out var sec) ? Math.Clamp(sec, 5, 600) : 20;
            Console.WriteLine($"Jogo: {game.Profile.Name} ({game.ProcessName}). Observando o tráfego por {seconds} s — fique na partida.");
            var pids = NetRoute.Windows.GameProcess.ProcessIds(game.ProcessName);
            using var observer = new NetRoute.Windows.EtwConnectionObserver();
            var candidates = await NetRouteEngine.DiscoverAsync(observer, pids.Contains, TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            if (candidates.Count == 0) { Console.Error.WriteLine("Não vi tráfego de partida (o jogo está no menu?). Entre numa partida e rode de novo."); return CliExit.NotFound; }
            var learned = LearnedPrefixStore.Load(NetRouteSettings.LearnedPath);
            foreach (var c in candidates.Take(4)) learned.Learn(gameId, c.Address, DateTimeOffset.UtcNow, profile.BrazilServerPlace);
            learned.Save(NetRouteSettings.LearnedPath);
            server = candidates[0].Address;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Servidor da partida: {server} porta {candidates[0].Port} ({candidates[0].Protocol}, {candidates[0].PacketsPerSecond:0} pacotes/s)."));
        }
        if (user is null) Console.WriteLine("Dica: defina sua cidade (nexus rota cidade <cidade>) para a régua física.");
        Console.WriteLine("Medindo e rastreando a rota (uns 30 s)...");
        var hint = profile?.BrazilServerPlace is { } sp && user is { InBrazil: true } ? Places.Find(sp) : null;
        var report = await engine.DiagnoseAsync(server!, user, hint).ConfigureAwait(false);
        if (o.ContainsKey("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                server = report.Server.ToString(), game = gameId, verdict = report.Verdict.ToString(), excessMs = report.ExcessMs, floorMs = report.FloorMs,
                goodUpperMs = report.GoodUpperMs, p50 = report.Direct.P50Ms, p95 = report.Direct.P95Ms, p99 = report.Direct.P99Ms, loss = report.Direct.LossPercent,
                findings = report.Findings.Select(f => new { kind = f.Kind.ToString(), f.Ttl, f.Text }),
                hops = report.Trace?.Hops.Select(h => new { h.Ttl, address = h.Address?.ToString(), h.HostName, best = h.BestMs }),
                summary = report.Summary,
            }, Json));
            return CliExit.Ok;
        }
        Console.WriteLine();
        Console.WriteLine(report.Summary);
        Console.WriteLine($"Medição direta: {report.Direct.Describe()}");
        foreach (var f in report.Findings) Console.WriteLine($"  • {f.Text}");
        if (report.Trace is { } t)
        {
            Console.WriteLine("Rota:");
            foreach (var h in t.Hops)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {h.Ttl,2}  {(h.BestMs is { } b ? $"{b,6:0.0} ms" : "     * ")}  {h.Address?.ToString() ?? "(sem resposta)"}{(h.HostName is null ? "" : $"  {h.HostName}")}"));
        }
        if (report.Verdict is RouteVerdict.ModerateExcess or RouteVerdict.HighExcess or RouteVerdict.LossOnPath)
            Console.WriteLine("Próximo passo: com um relay cadastrado, rode 'nexus rota testar' para medir com e sem.");
        return CliExit.Ok;
    }

    // ------------------------------------------------------------------ chave e relay (aplicador de administrador)

    private static int Code(Dictionary<string, string> o)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported();
        if (Store.ReadPublicKey() is null)
        {
            Console.WriteLine("Criando a chave deste PC (o Windows vai pedir permissão de administrador).");
            var r = Admin(o)?.Execute([new ActionRequest("netroute.identity.create@1")], "cli-rota");
            if (!Succeeded(r)) return Fail(r, "Não foi possível criar a chave.");
        }
        var pub = Store.ReadPublicKey();
        if (pub is null) { Console.Error.WriteLine("A chave foi criada, mas não consegui ler o código (client.pub)."); return CliExit.Partial; }
        Console.WriteLine("Código deste PC (só a parte pública; a chave privada nunca sai do PC):");
        Console.WriteLine(pub);
        Console.WriteLine();
        Console.WriteLine("No servidor do relay, rode:");
        Console.WriteLine($"  sudo python3 nexus_relay.py add {pub} --label \"{Environment.MachineName}\"");
        Console.WriteLine("e depois aqui: nexus rota relay adicionar <link que ele imprimir>");
        return CliExit.Ok;
    }

    private static int AddRelay(string link, Dictionary<string, string> o)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported();
        var (parsed, problem) = RelayLink.Parse(link);
        if (parsed is null) { Console.Error.WriteLine(problem); return CliExit.Usage; }
        var pub = Store.ReadPublicKey();
        if (pub is null) { Console.Error.WriteLine("Crie primeiro a chave deste PC: nexus rota relay codigo"); return CliExit.NotFound; }
        if (!parsed.AuthorizedFor(pub)) { Console.Error.WriteLine("Este link foi gerado para outra chave. No relay, rode 'add' com o código deste PC (nexus rota relay codigo)."); return CliExit.Usage; }
        Console.WriteLine($"Relay: {parsed.Name} — {parsed.Endpoint}{(Places.Find(parsed.Place) is { } p ? $" ({p.Name})" : "")}");
        Console.WriteLine("Vou instalar o cliente WireGuard oficial (se faltar, baixado de download.wireguard.com com assinatura conferida) e cadastrar o relay.");
        if (!Confirm(o, "Continuar?")) return CliExit.Cancelled;
        var r = Admin(o)?.Execute(
        [
            new ActionRequest("netroute.client.install@1"),
            new ActionRequest("netroute.relay.add@1", null, new Dictionary<string, string> { ["link"] = parsed.ToLink() }),
        ], "cli-rota");
        if (!Succeeded(r)) return Fail(r, "O cadastro não foi concluído.");
        var s = NetRouteSettings.Load(NetRouteSettings.DefaultPath) with { RelayId = parsed.Id };
        s.Save(NetRouteSettings.DefaultPath);
        Console.WriteLine($"Relay {parsed.Id} cadastrado. Agora: nexus rota testar");
        return CliExit.Ok;
    }

    // ------------------------------------------------------------------ teste A/B e túnel

    private static RelayLink? PickRelay(Dictionary<string, string> o)
    {
        var relays = Store.ReadRelays();
        var id = o.GetValueOrDefault("--relay") ?? NetRouteSettings.Load(NetRouteSettings.DefaultPath).RelayId;
        return relays.FirstOrDefault(r => r.Id == id) ?? (relays.Count == 1 ? relays[0] : null);
    }

    private static async Task<int> TestAsync(Dictionary<string, string> o)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported();
        var relay = PickRelay(o);
        if (relay is null) { Console.Error.WriteLine("Nenhum relay escolhido (nexus rota relay adicionar <link>, ou --relay <id>)."); return CliExit.NotFound; }
        var settings = NetRouteSettings.Load(NetRouteSettings.DefaultPath);
        if (settings.TunnelRunId is not null) { Console.Error.WriteLine("O túnel está ligado: desligue antes de testar (nexus rota desligar)."); return CliExit.Usage; }

        IPAddress? target;
        if (o.TryGetValue("--alvo", out var alvo)) { if (!IPAddress.TryParse(alvo, out target) || !PublicAddress.IsPublic(target)) { Console.Error.WriteLine("--alvo precisa ser um IP público."); return CliExit.Usage; } }
        else
        {
            var gameId = o.GetValueOrDefault("--jogo") ?? NetRoute.Windows.GameProcess.Find()?.GameId;
            var learned = gameId is null ? [] : LearnedPrefixStore.Load(NetRouteSettings.LearnedPath).Entries(gameId);
            // O último servidor visto do bloco mais frequente (o da próxima partida tende a estar na mesma sala).
            var first = learned.OrderByDescending(x => x.Sightings).FirstOrDefault(x => x.LastAddress is not null);
            if (first is null || !IPAddress.TryParse(first.LastAddress, out target)) { Console.Error.WriteLine("Sem servidor conhecido: rode 'nexus rota diagnosticar' durante uma partida, ou use --alvo <IP>."); return CliExit.NotFound; }
        }

        var engine = new NetRouteEngine(new IcmpPathProber());
        var user = Places.Find(settings.City);
        Console.WriteLine($"Medindo a rota direta até {target}...");
        var report = await engine.DiagnoseAsync(target!, user, null).ConfigureAwait(false);
        if (NetRouteEngine.ComparableTarget(report) is not { } comparable) { Console.Error.WriteLine("Nem o alvo nem os últimos roteadores respondem: não dá para comparar."); return CliExit.Partial; }
        if (!comparable.IsServer) Console.WriteLine($"O alvo não responde a ping; comparando pelo último roteador antes dele ({comparable.Target}).");

        Console.WriteLine($"Ligando o túnel em modo medição (o jogo continua na rota direta) pelo relay {relay.Name}.");
        var on = Admin(o)?.Execute([TunnelRequest(relay.Id, "generic", PrefixList.None)], "cli-rota-teste");
        if (!Succeeded(on) || on!.Response?.RunId is not { } run) return Fail(on, "Não foi possível ligar o modo medição.");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var agent = new RelayAgentClient(http, relay.AgentEndPoint);
            if (!await agent.HealthAsync().ConfigureAwait(false))
            {
                Console.Error.WriteLine("O agente do relay não respondeu pelo túnel. Confira no servidor: porta UDP liberada e 'nexus_relay.py status'.");
                return CliExit.Partial;
            }
            var progress = new Progress<string>(m => Console.WriteLine("  " + m));
            var result = await engine.AbTestAsync(comparable.Target, comparable.IsServer, relay, agent, progress).ConfigureAwait(false);
            Console.WriteLine();
            for (var i = 0; i < result.Rounds.Count; i++)
                Console.WriteLine($"  Rodada {i + 1}: direto {result.Rounds[i].Direct.Describe()} | relay {result.Rounds[i].Relay.Describe()}");
            Console.WriteLine();
            Console.WriteLine(result.Verdict.Text);
            if (result.Verdict.UseRelay) Console.WriteLine("Para usar: nexus rota ligar (antes de entrar na partida).");
            return CliExit.Ok;
        }
        finally
        {
            var off = Admin(o)?.RevertRun(run);
            Console.WriteLine(off is { Ok: true } ? "Modo medição desligado." : $"Atenção: não consegui desligar o modo medição ({off?.Message}). Rode: nexus revert {run}");
        }
    }

    private static async Task<int> OnAsync(Dictionary<string, string> o)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported();
        var relay = PickRelay(o);
        if (relay is null) { Console.Error.WriteLine("Nenhum relay escolhido (nexus rota relay adicionar <link>, ou --relay <id>)."); return CliExit.NotFound; }
        var settings = NetRouteSettings.Load(NetRouteSettings.DefaultPath);
        if (settings.TunnelRunId is not null) { Console.Error.WriteLine("O túnel já está ligado (nexus rota desligar)."); return CliExit.Usage; }
        var gameId = o.GetValueOrDefault("--jogo") ?? NetRoute.Windows.GameProcess.Find()?.GameId;
        if (gameId is null || !NetRoute.WireGuard.TunnelConfig.IsSafeId(gameId)) { Console.Error.WriteLine("Diga o jogo: --jogo valorant|cs2|fortnite|game-<processo>."); return CliExit.Usage; }

        IReadOnlyList<IpPrefix> prefixes;
        if (gameId == GameNetProfiles.CounterStrike2.Id)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var pops = await ValveSdr.FetchAsync(http).ConfigureAwait(false);
            prefixes = PrefixList.FitToParameter(ValveSdr.PrefixesFor(pops, ValveSdr.SaoPauloPop));
        }
        else
        {
            var store = LearnedPrefixStore.Load(NetRouteSettings.LearnedPath);
            var byPriority = store.Entries(gameId).OrderByDescending(x => x.Sightings).ThenByDescending(x => x.LastSeen)
                .Select(x => IpPrefix.TryParse(x.Prefix, out var p) ? p : (IpPrefix?)null).Where(p => p is not null).Select(p => p!.Value);
            prefixes = PrefixList.FitToParameter(byPriority);
        }
        if (prefixes.Count == 0) { Console.Error.WriteLine("Nenhum bloco conhecido para este jogo: rode 'nexus rota diagnosticar' durante uma partida primeiro."); return CliExit.NotFound; }
        if (!o.ContainsKey("--sem-teste"))
            Console.WriteLine("Lembrete: ligue só se 'nexus rota testar' mostrou ganho (ou use --sem-teste para ligar mesmo assim).");
        Console.WriteLine($"Túnel para {gameId} pelo relay {relay.Name}: {prefixes.Count} bloco(s) ({PrefixList.Format(prefixes)}).");
        Console.WriteLine("Só o tráfego para esses blocos passa pelo relay; o resto do PC continua na rota normal. Ligue ANTES de entrar na partida.");
        if (!Confirm(o, "Ligar?")) return CliExit.Cancelled;
        var r = Admin(o)?.Execute([TunnelRequest(relay.Id, gameId, PrefixList.Format(prefixes))], "cli-rota");
        if (!Succeeded(r) || r!.Response?.RunId is not { } run) return Fail(r, "O túnel não foi ligado.");
        (settings with { TunnelRunId = run, TunnelGame = gameId, TunnelSince = DateTimeOffset.Now }).Save(NetRouteSettings.DefaultPath);
        Console.WriteLine($"Rota ligada. Para desligar: nexus rota desligar (ou nexus revert {run}).");
        return CliExit.Ok;
    }

    private static ActionRequest TunnelRequest(string relayId, string gameId, string prefixes) =>
        new ActionRequest("netroute.tunnel.on@1", null, new Dictionary<string, string> { ["relay"] = relayId, ["game"] = gameId, ["prefixes"] = prefixes });

    private static int Off(Dictionary<string, string> o)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported();
        var settings = NetRouteSettings.Load(NetRouteSettings.DefaultPath);
        if (settings.TunnelRunId is not { } run) { Console.WriteLine("O túnel não está ligado por este usuário (veja 'nexus history' se ligou por outro caminho)."); return CliExit.Ok; }
        var r = Admin(o)?.RevertRun(run);
        if (r is not { Ok: true }) return Fail(r, "Não foi possível desligar.");
        (settings with { TunnelRunId = null, TunnelGame = null, TunnelSince = null }).Save(NetRouteSettings.DefaultPath);
        Console.WriteLine("Rota desligada: o jogo voltou à rota direta.");
        return CliExit.Ok;
    }

    private static async Task<int> WatchAsync(Dictionary<string, string> o)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported();
        var settings = NetRouteSettings.Load(NetRouteSettings.DefaultPath);
        if (settings.TunnelRunId is null) { Console.Error.WriteLine("O túnel não está ligado (nexus rota ligar)."); return CliExit.Usage; }
        var relay = PickRelay(o);
        if (relay is null) { Console.Error.WriteLine("Relay do túnel não encontrado."); return CliExit.NotFound; }
        var prober = new IcmpPathProber();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var baseline = Nexus.NetRoute.Stats.LatencyStats.From(await prober.PingAsync(relay.AgentAddress, 10, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)).ConfigureAwait(false));
        if (baseline.P50Ms is not { } base50) { Console.Error.WriteLine("O relay não responde pelo túnel agora. Desligue a rota (nexus rota desligar)."); return CliExit.Partial; }
        var monitor = new Nexus.NetRoute.Decision.TunnelHealthMonitor(base50);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Vigiando o túnel até {relay.Name} (base {base50:0} ms). Ctrl+C para parar."));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var window = Nexus.NetRoute.Stats.LatencyStats.From(await prober.PingAsync(relay.AgentAddress, 20, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false));
                var action = monitor.Observe(window);
                Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {window.Describe()}{(monitor.BadStreak > 0 ? $"  (janela ruim {monitor.BadStreak}/{Nexus.NetRoute.Decision.TunnelHealthMonitor.BadWindowsToFallBack})" : "")}");
                if (action != Nexus.NetRoute.Decision.HealthAction.FallBackToDirect) continue;
                Console.WriteLine("O túnel piorou de forma consistente. A rota direta deve estar melhor agora.");
                if (!o.ContainsKey("--desligar-sozinho")) { Console.WriteLine("Para desligar: nexus rota desligar (a partida atual pode cair; faça entre partidas)."); return CliExit.Partial; }
                return Off(o);
            }
        }
        catch (OperationCanceledException) { }
        return CliExit.Ok;
    }

    // ------------------------------------------------------------------ apoio

    private static BrokerSession? Admin(Dictionary<string, string> o)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        return sid is null ? null : BrokerSession.ForThisPc(BrokerLevel.Admin, AppContext.BaseDirectory, sid, o.ContainsKey("--quiet") ? null : m => Console.Error.WriteLine(m));
    }

    private static bool Succeeded(BrokerCallResult? r) =>
        r is { Connected: true, Response: { Ok: true } resp } &&
        !(resp.Results ?? []).Any(x => x.Outcome is "Failed" or "FailedAndCompensated" or "Interrupted" or "Conflict" or "Refused" or "Blocked" or "NotApplicable");

    private static int Fail(BrokerCallResult? r, string what)
    {
        Console.Error.WriteLine($"{what} {r?.Message}".Trim());
        foreach (var x in r?.Response?.Results ?? []) Console.Error.WriteLine($"  {x.Outcome} {x.ActionKey}{(x.Detail is null ? "" : $" — {x.Detail}")}");
        return r is { Connected: true } ? CliExit.Partial : CliExit.BrokerRefused;
    }

    private static bool Confirm(Dictionary<string, string> o, string question)
    {
        if (o.ContainsKey("--yes")) return true;
        if (Console.IsInputRedirected) { Console.Error.WriteLine("Sem terminal para confirmar: use --yes. Nada foi alterado."); return false; }
        Console.Write($"{question} [s/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "s" or "sim" or "y" or "yes";
    }

    private static int Unsupported()
    {
        Console.Error.WriteLine("Esta parte usa o aplicador e o WireGuard do Windows.");
        return CliExit.Unsupported;
    }
}
