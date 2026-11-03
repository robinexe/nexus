using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using Nexus.Desktop.Views.Controls;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Gpu.Windows;
using Nexus.SystemTools;
using Nexus.SystemTools.Checks;

namespace Nexus.Desktop.ViewModels.Simple;

// =====================================================================================================================
// Checagens do PC (somente leitura) na interface: quem lê (uma vez por leitura do PC, fora da thread da interface), o que
// foi lido por último (PcCheckState) e os painéis das páginas avançadas (Rede para jogos em Ferramentas do sistema, Saúde dos
// discos em Disco). Nada aqui escreve no sistema; a única correção (taxa do monitor) vai pela ação reversível da página
// Placa de vídeo, com a contagem "Manter esta taxa?" de 15 s.
// =====================================================================================================================

/// <summary>Os leitores reais do Windows (PowerShell CIM/eventos, QueryDisplayConfig, NetworkInterface, ICMP).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPcCheckReaders : IPcCheckReaders
{
    private readonly WindowsProcessRunner _runner = new();
    private INetworkInfo? _network;

    public IReadOnlyList<DiskHealthFacts> Disks() => DiskHealthReader.Read(_runner);

    public IReadOnlyList<(string Name, string? InstallDir)> Games()
    {
        var notes = new List<string>();
        return [.. Nexus.Measurement.GameLibrary.Scan(Nexus.Measurement.Windows.WindowsGameRoots.Read(), notes).Select(g => (g.Name, g.InstallDir))];
    }

    public IReadOnlyList<DisplayCheckFacts> Displays() =>
        [.. GpuLabScanner.MonitorsOnly().Select(m => new DisplayCheckFacts(m.Device,
            !string.IsNullOrWhiteSpace(m.FriendlyName) ? m.FriendlyName : m.Internal ? Loc.T("Gpu.TelaNotebook") : m.Device,
            m.Current.Width, m.Current.Height, m.Current.Hz, m.Advice.Best?.Hz,
            m.Advice.BelowMaxRefresh ? m.Advice.Best?.Key : null, m.HdrSupported, m.HdrEnabled, m.Internal))];

    public ThrottleFacts Throttle(bool? onBattery) => ThrottleReader.Read(_runner, onBattery);

    public bool? OnBattery() => Nexus.Measurement.Windows.WindowsProcessProbe.OnBattery();

    public INetworkInfo Network => _network ??= new WindowsNetworkInfo(_runner);
    public IPingClient Ping { get; } = new SystemPingClient();
    public ILoadSource NewLoad() => new CloudflareLoadSource();
}

/// <summary>O coordenador da janela (páginas avançadas e telas simples usam o mesmo), com os leitores reais.</summary>
public sealed partial class PcChecksCoordinator
{
    private static PcChecksCoordinator? _shared;

    public static PcChecksCoordinator Shared => _shared ??= new PcChecksCoordinator(new WindowsPcCheckReaders());
}

/// <summary>Uma linha de achado nos painéis avançados: título, evidência, o que fazer e o tom.</summary>
public sealed record PcCheckFindingRow(string Title, string Evidence, string Advice, Tone Tone)
{
    public bool HasAdvice => Advice.Length > 0;

    public static PcCheckFindingRow From(DiagnosticFinding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var tone = f.Severity switch { DiagnosticSeverity.Attention => Tone.Warn, DiagnosticSeverity.Info => Tone.Info, _ => Tone.Good };
        return new PcCheckFindingRow(f.Title, f.Evidence, f.Owner?.Explanation ?? "", tone);
    }
}

/// <summary>
/// "Rede para jogos" (página Ferramentas do sistema): ping, variação e perda até o roteador e até o servidor que você digitar,
/// Wi-Fi ou cabo com o sinal, e o teste de bufferbloat com a conexão cheia (só depois de confirmar quanto de dados usa).
/// </summary>
public sealed class GameNetworkPanel : ObservableObject, IDisposable
{
    private readonly PcChecksCoordinator _checks;
    private readonly Func<IModuleHost?> _host;
    private readonly BufferbloatPlan _plan = new();
    private string _hostText = GameNetworkTest.DefaultHost;
    private string _status = "";
    private bool _running;
    private CancellationTokenSource? _cancel;
    private IReadOnlyList<PcCheckFindingRow> _rows = [];

    public GameNetworkPanel(PcChecksCoordinator checks, Func<IModuleHost?> host)
    {
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        TestCommand = new RelayCommand(() => _ = TestAsync(), () => !_running && GameNetworkTest.IsValidHost(_hostText));
        BloatCommand = new RelayCommand(() => _ = BloatAsync(), () => !_running && GameNetworkTest.IsValidHost(_hostText));
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => _running);
        _checks.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    /// <summary>Servidor do jogo (IP ou nome). Validado antes de usar: só letras, números, ponto, hífen e IP.</summary>
    public string HostText
    {
        get => _hostText;
        set { if (Set(ref _hostText, value ?? "")) { OnPropertyChanged(nameof(HostInvalid)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    }

    public bool HostInvalid => !GameNetworkTest.IsValidHost(_hostText);
    public string Status { get => _status; private set { if (Set(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    public bool Running { get => _running; private set { if (Set(ref _running, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public IReadOnlyList<PcCheckFindingRow> Rows { get => _rows; private set { if (Set(ref _rows, value)) OnPropertyChanged(nameof(HasRows)); } }
    public bool HasRows => _rows.Count > 0;
    public string BloatButtonText => Loc.F("Pc.Net.BloatButton", _plan.MaxMegabytes);
    public RelayCommand TestCommand { get; }
    public RelayCommand BloatCommand { get; }
    public RelayCommand CancelCommand { get; }

    public void Dispose()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
    }

    private void Refresh()
    {
        var net = _checks.State.Network;
        Rows = net is null ? [] : [.. PcChecks.Network(net).Select(PcCheckFindingRow.From)];
    }

    private async Task TestAsync()
    {
        Running = true;
        _cancel = new CancellationTokenSource();
        Status = Loc.T("Pc.Net.Testing");
        try
        {
            var target = _hostText.Trim();
            var r = _checks.Readers;
            var token = _cancel.Token;
            var facts = await Task.Run(() => GameNetworkTest.QuickAsync(r.Network, r.Ping, target, GameNetworkTest.DefaultCount, GameNetworkTest.DefaultInterval, token));
            _checks.Update(s => s with { Network = facts with { Bufferbloat = s.Network?.Bufferbloat } });
            Status = Loc.T("Pc.Net.Done");
        }
        catch (OperationCanceledException) { Status = Loc.T("Pc.Net.Cancelled"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.F("Pc.Net.Failed", ex.Message); }
        finally { Running = false; _cancel?.Dispose(); _cancel = null; }
    }

    private async Task BloatAsync()
    {
        if (_host() is not { } host || !host.Confirm(Loc.T("Pc.Net.BloatTitle"), Loc.F("Pc.Net.BloatConfirm", _plan.MaxMegabytes))) return;
        Running = true;
        _cancel = new CancellationTokenSource();
        try
        {
            var target = _hostText.Trim();
            var r = _checks.Readers;
            var progress = new Progress<string>(p => Status = p switch
            {
                "download" => Loc.T("Pc.Net.PhaseDownload"),
                "upload" => Loc.T("Pc.Net.PhaseUpload"),
                _ => Loc.T("Pc.Net.PhaseIdle"),
            });
            var token = _cancel.Token;
            var result = await Task.Run(async () =>
            {
                var load = r.NewLoad();
                try { return await BufferbloatTest.RunAsync(r.Ping, load, target, _plan, progress, token).ConfigureAwait(false); }
                finally { (load as IDisposable)?.Dispose(); }
            });
            var link = r.Network.Active();
            _checks.Update(s => s with
            {
                Network = (s.Network ?? new NetworkCheckFacts(link?.Kind ?? LinkKind.Unknown, link?.Name, null, link?.SpeedMbps)) with { Bufferbloat = result },
            });
            Status = Loc.F("Pc.Net.BloatDone", result.Grade, Math.Round(result.BytesUsed / 1_000_000.0));
        }
        catch (OperationCanceledException) { Status = Loc.T("Pc.Net.Cancelled"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.F("Pc.Net.Failed", ex.Message); }
        finally { Running = false; _cancel?.Dispose(); _cancel = null; }
    }
}

/// <summary>Um disco na "Saúde dos discos": cada campo lido ou "não informado".</summary>
public sealed record DiskHealthRow(string Name, string Type, string Health, string Temperature, string Wear, string Errors, string Hours, Tone Tone);

/// <summary>"Saúde dos discos" (página Disco): estado, temperatura, desgaste, erros e horas ligado de cada disco físico.</summary>
public sealed class DiskHealthPanel : ObservableObject
{
    private readonly PcChecksCoordinator _checks;
    private IReadOnlyList<DiskHealthRow> _disks = [];
    private IReadOnlyList<PcCheckFindingRow> _notes = [];
    private bool _reading;

    public DiskHealthPanel(PcChecksCoordinator checks)
    {
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        RefreshCommand = new RelayCommand(() => _ = ReadAsync(), () => !_reading);
        _checks.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public IReadOnlyList<DiskHealthRow> Disks { get => _disks; private set { if (Set(ref _disks, value)) { OnPropertyChanged(nameof(HasDisks)); OnPropertyChanged(nameof(NoDisks)); } } }
    public bool HasDisks => _disks.Count > 0;
    public bool NoDisks => !_reading && _disks.Count == 0;
    /// <summary>Achados que pedem algo (disco falhando, quente, jogos em HD).</summary>
    public IReadOnlyList<PcCheckFindingRow> Notes { get => _notes; private set { if (Set(ref _notes, value)) OnPropertyChanged(nameof(HasNotes)); } }
    public bool HasNotes => _notes.Count > 0;
    public bool Reading { get => _reading; private set { if (Set(ref _reading, value)) { OnPropertyChanged(nameof(NoDisks)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } } }
    public RelayCommand RefreshCommand { get; }

    private void Refresh()
    {
        var s = _checks.State;
        var disks = s.Disks ?? [];
        Disks = [.. disks.Select(Row)];
        Notes = [.. PcChecks.Disks(disks, s.Games).Where(f => f.Severity != DiagnosticSeverity.Ok).Select(PcCheckFindingRow.From)];
    }

    public static DiskHealthRow Row(DiskHealthFacts d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var sev = PcChecks.Disk(d).Severity;
        var errors = d.ReadErrorsUncorrected is null && d.WriteErrorsUncorrected is null
            ? Loc.T("Pc.NotInformed") : ((d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0)).ToString(Loc.Culture);
        var health = d.Health?.ToLowerInvariant() switch
        {
            "healthy" => Loc.T("Pc.Disk.Healthy"),
            "warning" => Loc.T("Pc.Disk.Warning"),
            "unhealthy" => Loc.T("Pc.Disk.Unhealthy"),
            _ => Loc.T("Pc.NotInformed"),
        };
        return new DiskHealthRow(PcCheckText.DiskName(d), StorageClassifier.Describe(d.Media, d.Bus), health,
            PcCheckText.OrNotInformed(d.TemperatureC, "{0} °C"), PcCheckText.OrNotInformed(d.WearPercent, "{0}%"), errors,
            PcCheckText.OrNotInformed(d.PowerOnHours, "{0:N0} h"), sev == DiagnosticSeverity.Attention ? Tone.Warn : Tone.Good);
    }

    private async Task ReadAsync()
    {
        Reading = true;
        try
        {
            var r = _checks.Readers;
            var disks = await Task.Run(() =>
            {
                try { return r.Disks(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
            });
            if (disks is not null) _checks.Update(s => s with { Disks = disks });
        }
        finally { Reading = false; }
    }
}

/// <summary>"Usar 144 Hz" das telas simples: abre a página Placa de vídeo e segue o caminho de lá (confirmação, ação reversível e "Manter esta taxa?" de 15 s).</summary>
public static class MonitorFix
{
    public static async Task RunAsync(IModuleHost? host, string device)
    {
        if (host?.FindPage(typeof(GpuLabPage)) is not GpuLabPage page) return;
        host.Navigate(page);
        await page.FixRefreshAsync(device);
    }
}
