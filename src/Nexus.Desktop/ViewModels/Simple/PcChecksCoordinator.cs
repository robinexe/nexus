using Nexus.HardwareDiscovery;
using Nexus.SystemTools.Checks;

namespace Nexus.Desktop.ViewModels.Simple;

// =====================================================================================================================
// Checagens do PC: quem lê e o que foi lido por último (lógica sem WPF, testada em PcChecksDesktopTests com leitores falsos).
// Os leitores reais e os painéis das páginas avançadas ficam em PcCheckPanels.cs.
// =====================================================================================================================

/// <summary>Leitores das checagens (trocáveis nos testes).</summary>
public interface IPcCheckReaders
{
    IReadOnlyList<DiskHealthFacts> Disks();
    IReadOnlyList<(string Name, string? InstallDir)> Games();
    IReadOnlyList<DisplayCheckFacts> Displays();
    ThrottleFacts Throttle(bool? onBattery);
    bool? OnBattery();
    INetworkInfo Network { get; }
    IPingClient Ping { get; }
    ILoadSource NewLoad();
}

/// <summary>
/// Junta as checagens: lê disco/monitor/processador/rede local depois de cada leitura do PC, guarda o resultado do teste de rede
/// que o usuário roda, e avisa a janela (Changed) para os achados entrarem no Diagnóstico, na tela Início e em Problemas.
/// </summary>
public sealed partial class PcChecksCoordinator
{
    private readonly IPcCheckReaders _readers;
    private readonly object _gate = new();
    private PcCheckState _state = new();
    private IReadOnlyList<DiagnosticFinding> _findings = [];
    private IReadOnlyDictionary<string, PlainFinding> _plain = new Dictionary<string, PlainFinding>();
    private DateTimeOffset? _lastBatteryNotice;

    public PcChecksCoordinator(IPcCheckReaders readers) => _readers = readers ?? throw new ArgumentNullException(nameof(readers));

    public IPcCheckReaders Readers => _readers;
    public PcCheckState State { get { lock (_gate) return _state; } }
    public IReadOnlyList<DiagnosticFinding> Findings { get { lock (_gate) return _findings; } }
    public IReadOnlyDictionary<string, PlainFinding> Plain { get { lock (_gate) return _plain; } }

    /// <summary>Algo mudou (chamado na thread de quem atualizou; a janela volta para a interface).</summary>
    public event EventHandler? Changed;

    /// <summary>"Na bateria o FPS cai bastante; ligue na tomada" (título, texto).</summary>
    public event EventHandler<(string Title, string Text)>? Notice;

    public void Update(Func<PcCheckState, PcCheckState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            _state = change(_state);
            _findings = _state.Findings();
            _plain = PcCheckText.Plain(_state);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Leitura automática (depois de cada leitura do PC): discos + jogos, monitores, limitação do processador e a rede local
    /// (Wi-Fi/cabo e ping ao roteador; nada sai da sua casa). Cada parte falha sozinha.
    /// </summary>
    public async Task RunAutomaticAsync(IReadOnlyList<PhysicalDiskFacts> physical)
    {
        ArgumentNullException.ThrowIfNull(physical);
        var disks = Task.Run(() =>
        {
            var d = Guard(_readers.Disks, null);
            var games = d is null ? [] : PcChecks.PlaceGames(Guard(_readers.Games, []), physical);
            return (d, games);
        });
        var displays = Task.Run(() => Guard(_readers.Displays, null));
        var throttle = Task.Run(() => Guard(() => _readers.Throttle(_readers.OnBattery()), null));
        var network = Task.Run(async () =>
        {
            try { return (NetworkCheckFacts?)await GameNetworkTest.QuickAsync(_readers.Network, _readers.Ping, null, 10, GameNetworkTest.DefaultInterval).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        });
        var (diskFacts, games) = await disks.ConfigureAwait(true);
        var displayFacts = await displays.ConfigureAwait(true);
        var throttleFacts = await throttle.ConfigureAwait(true);
        var local = await network.ConfigureAwait(true);
        Update(s => s with
        {
            Disks = diskFacts ?? s.Disks,
            Games = diskFacts is null ? s.Games : games,
            Displays = displayFacts ?? s.Displays,
            Throttle = throttleFacts ?? s.Throttle,
            // O teste completo que o usuário rodou (servidor, bufferbloat) vale mais que o automático; o automático só preenche o vazio.
            Network = s.Network ?? local,
        });
    }

    private static T Guard<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return fallback; }
    }

    /// <summary>Um jogo começou (detecção do Medir): avisa se o notebook está na bateria e o aviso está ligado em Ajustes.</summary>
    public bool OnGameStarted(string game, bool enabled, DateTimeOffset now)
    {
        bool? onBattery;
        try { onBattery = _readers.OnBattery(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { onBattery = null; }
        if (!PcChecks.ShouldNotifyBattery(enabled, null, onBattery, _lastBatteryNotice, now)) return false;
        _lastBatteryNotice = now;
        Notice?.Invoke(this, (Loc.T("Pc.Battery.Title"), string.IsNullOrWhiteSpace(game) ? Loc.T("Pc.Battery.Text") : game + " — " + Loc.T("Pc.Battery.Text")));
        return true;
    }
}
