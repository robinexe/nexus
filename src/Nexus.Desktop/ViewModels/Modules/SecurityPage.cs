using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Nexus.Broker;
using Nexus.Security;

namespace Nexus.Desktop.ViewModels;

/// <summary>Linha do placar de proteção. <see cref="ToneKey"/>: Good, Warn, Bad, Unknown (a página pinta com DataTrigger).</summary>
public sealed class ProtectionRow(ProtectionItem item, RelayCommand? open)
{
    public string Name => item.Name;
    public string State => item.State;
    public string Detail => item.Detail;
    public string? Advice => item.Advice;
    public string ToneKey => item.Tone.ToString();
    public RelayCommand? OpenCommand => open;
}

/// <summary>Ponto suspeito para revisão. Só tem botão de correção quando existe ação reversível no catálogo.</summary>
public sealed class FindingRow(Finding f, RelayCommand? fix)
{
    public string Category => f.Category;
    public string Title => f.Title;
    public string Evidence => f.Evidence;
    public string Why => f.Why;
    public string HowToVerify => f.HowToVerify;
    public string Suggested => f.Suggested;
    public string SeverityText => f.Severity switch { Severity.High => "Alta", Severity.Medium => "Média", Severity.Low => "Baixa", _ => "Info" };
    public string ToneKey => f.Severity switch { Severity.High => "Bad", Severity.Medium => "Warn", Severity.Low => "Unknown", _ => "Info" };
    public RelayCommand? FixCommand => fix;
}

public sealed class ThreatRow(ThreatRecord t)
{
    public string Name => t.Name;
    public string Meta => $"{t.Severity} · {t.Status}" + (t.Detected is { } d ? $" · {d.ToLocalTime():dd/MM/yyyy HH:mm}" : "");
    public string Resources => t.Resources.Count == 0 ? "" : string.Join("\n", t.Resources);
    public string Recommendation => t.Recommendation;
    public string ToneKey => t.Active || t.Recommendation.StartsWith("Ação", StringComparison.Ordinal) ? "Bad" : "Good";
}

public sealed class OrphanRow(RegistryOrphan o) : ObservableObject
{
    private bool _selected;
    public RegistryOrphan Orphan => o;
    public string Category => o.Category;
    public string Display => o.Display;
    public string Reason => o.Reason;
    public string Location => o.ValueName is null ? o.Key.ToString() : $"{o.Key} → {Text(o.ValueName)}";
    public bool Removable => o.Removable;
    public string ScopeText => o.Scope == RegScope.Machine ? "Todo o PC (pede administrador)" : "Seu usuário";
    public bool IsSelected { get => _selected; set => Set(ref _selected, value && o.Removable); }
    private static string Text(string s) => s.Length == 0 ? "(Padrão)" : s;
}

/// <summary>
/// Segurança: placar do Microsoft Defender e do Windows (somente leitura), verificações do próprio Defender (MpCmdRun,
/// visível e cancelável), pontos suspeitos para revisão (nunca remoção automática) e o inspetor de registro (sobras
/// escolhidas, com cópia e desfazer, explicando que isso NÃO acelera o PC). Toda escrita passa pelo Host (aplicador).
/// </summary>
public sealed class SecurityPage : ModulePage
{
    private const int MaxSelection = 28;
    private bool _loaded;
    private bool _scanning;
    private bool _inspecting;
    private string _headline = "Lendo o estado de proteção…";
    private string _headlineTone = "Unknown";
    private string _scoreText = "";
    private string _readAt = "";
    private string _scanStatus = "";
    private string _scanLine = "";
    private string _scanElapsed = "";
    private string _inspectorStatus = "O inspetor só roda quando você pede.";
    private Guid? _lastRun;
    private BrokerLevel _lastLevel;
    private Action? _cancelScan;

    public SecurityPage()
    {
        RefreshCommand = new RelayCommand(() => _ = RunAsync(LoadAsync), () => !Busy && !_scanning);
        QuickScanCommand = new RelayCommand(() => _ = ScanAsync(ScanKind.Quick), CanScan);
        FullScanCommand = new RelayCommand(() => _ = ScanAsync(ScanKind.Full), CanScan);
        CustomScanCommand = new RelayCommand(() => _ = ScanAsync(ScanKind.Custom), CanScan);
        UpdateSignaturesCommand = new RelayCommand(() => _ = UpdateAsync(), CanScan);
        CancelScanCommand = new RelayCommand(() => _cancelScan?.Invoke(), () => _scanning);
        OpenSecurityCommand = new RelayCommand(() => Open(SecurityStatus.UriThreat));
        OpenHistoryCommand = new RelayCommand(() => Open(SecurityStatus.UriThreat));
        InspectCommand = new RelayCommand(() => _ = InspectAsync(), () => !_inspecting && !Busy);
        RemoveOrphansCommand = new RelayCommand(() => _ = RemoveOrphansAsync(), () => !Busy && Orphans.Any(o => o.IsSelected));
        SelectNoneCommand = new RelayCommand(() => { foreach (var o in Orphans) o.IsSelected = false; });
        UndoCommand = new RelayCommand(() => _ = UndoAsync(), () => !Busy && _lastRun is not null);
    }

    public override string Title => "Segurança";
    public override string Group => "Proteger";
    public override string Subtitle => "Verifique ameaças com o Microsoft Defender e encontre pontos suspeitos.";

    public ObservableCollection<ProtectionRow> Protection { get; } = [];
    public ObservableCollection<FindingRow> Findings { get; } = [];
    public ObservableCollection<ThreatRow> Threats { get; } = [];
    public ObservableCollection<OrphanRow> Orphans { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<string> ScanOutput { get; } = [];

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string HeadlineTone { get => _headlineTone; private set => Set(ref _headlineTone, value); }
    public string ScoreText { get => _scoreText; private set => Set(ref _scoreText, value); }
    public string ReadAt { get => _readAt; private set => Set(ref _readAt, value); }
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }
    public string ScanLine { get => _scanLine; private set => Set(ref _scanLine, value); }
    public string ScanElapsed { get => _scanElapsed; private set => Set(ref _scanElapsed, value); }
    public string InspectorStatus { get => _inspectorStatus; private set => Set(ref _inspectorStatus, value); }
    public bool Scanning { get => _scanning; private set => Set(ref _scanning, value); }
    public bool Inspecting { get => _inspecting; private set => Set(ref _inspecting, value); }
    public bool CanUndo => _lastRun is not null;
    public int FindingCount => Findings.Count;
    public int ThreatCount => Threats.Count;
    public int OrphanCount => Orphans.Count;
    public string FindingSummary => Findings.Count == 0 ? "Nenhum ponto suspeito encontrado nas verificações do NEXUS." :
        $"{Findings.Count(f => f.ToneKey == "Bad")} de gravidade alta, {Findings.Count(f => f.ToneKey == "Warn")} média, {Findings.Count(f => f.ToneKey is "Unknown" or "Info")} baixa/informativa.";

    public RelayCommand RefreshCommand { get; }
    public RelayCommand QuickScanCommand { get; }
    public RelayCommand FullScanCommand { get; }
    public RelayCommand CustomScanCommand { get; }
    public RelayCommand UpdateSignaturesCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand OpenSecurityCommand { get; }
    public RelayCommand OpenHistoryCommand { get; }
    public RelayCommand InspectCommand { get; }
    public RelayCommand RemoveOrphansCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand UndoCommand { get; }

    public override Task OnShownAsync() => _loaded ? Task.CompletedTask : RunAsync(LoadAsync);

    private bool CanScan() => !_scanning && !Busy;

    // ------------------------------------------------------------------ leitura

    private async Task LoadAsync()
    {
        Headline = "Lendo o estado de proteção…";
        HeadlineTone = "Unknown";
        var report = await Task.Run(() => WindowsSecurityReader.Read());
        _loaded = true;
        Protection.Clear();
        foreach (var p in report.Protection)
            Protection.Add(new ProtectionRow(p, p.OpenUri is { } uri ? new RelayCommand(() => Open(uri)) : null));
        Findings.Clear();
        foreach (var f in report.Findings)
            Findings.Add(new FindingRow(f, f.FixActionId is { } id ? new RelayCommand(() => _ = FixAsync(f, id), () => !Busy) : null));
        Threats.Clear();
        foreach (var t in report.Threats) Threats.Add(new ThreatRow(t));
        Notes.Clear();
        foreach (var n in report.Notes) Notes.Add(n);

        var bad = report.Protection.Count(p => p.Tone == Tone.Bad);
        var warn = report.Protection.Count(p => p.Tone == Tone.Warn);
        var good = report.Protection.Count(p => p.Tone == Tone.Good);
        var activeThreats = report.Threats.Count(t => t.Active || t.Recommendation.StartsWith("Ação", StringComparison.Ordinal));
        (Headline, HeadlineTone) = (bad > 0 || activeThreats > 0) ? ("Em risco: há proteção desligada ou ameaça pendente", "Bad")
            : warn > 0 ? ("Protegido, com pontos de atenção", "Warn")
            : ("Protegido", "Good");
        ScoreText = $"{good} de {report.Protection.Count} itens em verde · {warn} em atenção · {bad} em vermelho";
        ReadAt = $"Lido em {report.ReadAt.ToLocalTime():dd/MM/yyyy HH:mm}";
        Message = "";
        OnPropertyChanged(nameof(FindingCount));
        OnPropertyChanged(nameof(ThreatCount));
        OnPropertyChanged(nameof(FindingSummary));
    }

    private static void Open(string uri)
    {
        // Só URIs fixas do próprio Windows (windowsdefender://, ms-settings:), nunca texto de fora.
        if (!uri.StartsWith("windowsdefender://", StringComparison.Ordinal) && !uri.StartsWith("ms-settings:", StringComparison.Ordinal)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ verificações (Defender)

    private async Task ScanAsync(ScanKind kind)
    {
        if (Host is null) return;
        string? path = null;
        if (kind == ScanKind.Custom)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha a pasta para o Defender verificar" };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FolderName;
        }
        var what = kind switch
        {
            ScanKind.Quick => "uma verificação rápida (alguns minutos)",
            ScanKind.Full => "uma verificação completa (pode levar mais de uma hora e deixar o PC mais lento enquanto roda)",
            _ => $"a verificação de \"{path}\"",
        };
        if (!Host.Confirm("Verificar com o Microsoft Defender",
                $"O NEXUS vai pedir ao Microsoft Defender {what}, usando a ferramenta oficial MpCmdRun.\n\nNada é alterado nas configurações. Se algo for encontrado, a remoção ou quarentena é feita pelo próprio Defender (Segurança do Windows).\n\nContinuar?"))
            return;
        await RunDefenderAsync(kind == ScanKind.Full ? "Verificação completa" : kind == ScanKind.Quick ? "Verificação rápida" : "Verificação personalizada",
            (progress, ct) => DefenderScanner.ScanAsync(kind, path, progress, ct));
    }

    private async Task UpdateAsync()
    {
        if (Host is null || !Host.Confirm("Atualizar assinaturas", "Pedir ao Microsoft Defender para baixar as assinaturas mais recentes (MpCmdRun -SignatureUpdate)? Precisa de internet.")) return;
        await RunDefenderAsync("Atualização de assinaturas", DefenderScanner.UpdateSignaturesAsync);
    }

    private async Task RunDefenderAsync(string label, Func<Action<string, TimeSpan>, CancellationToken, Task<DefenderRunResult>> run)
    {
        Scanning = true;
        ScanStatus = $"{label} em andamento… O Defender não informa porcentagem; o tempo e as mensagens dele aparecem aqui.";
        ScanLine = "";
        ScanOutput.Clear();
        using var cts = new CancellationTokenSource();
        _cancelScan = cts.Cancel;
        var dispatcher = Application.Current?.Dispatcher;
        try
        {
            var result = await run((line, elapsed) =>
            {
                ScanElapsed = elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
                if (line.Length == 0) return;
                ScanLine = line;
                dispatcher?.BeginInvoke(() => { if (ScanOutput.Count < 200) ScanOutput.Add(line); });
            }, cts.Token);
            ScanStatus = $"{label}: {result.Summary}";
            Host?.Notify(result.Summary);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ScanStatus = $"{label}: não foi possível concluir ({ex.Message}).";
        }
        finally
        {
            Scanning = false;
            _cancelScan = null;
        }
        await RunAsync(LoadAsync); // assinaturas, última verificação e histórico mudam
    }

    // ------------------------------------------------------------------ correções reversíveis

    private static BrokerLevel LevelOf(string actionId) =>
        actionId is "security.persistence.user.remove" or "security.registry.orphans.user" ? BrokerLevel.User : BrokerLevel.Admin;

    private async Task FixAsync(Finding f, string actionId)
    {
        if (Host is null) return;
        var args = f.FixSelection is { } sel ? new Dictionary<string, string> { ["selection"] = sel } : null;
        var level = LevelOf(actionId);
        var text = $"{f.Title}\n\n{f.Evidence}\n\nO que o NEXUS faz: {f.Suggested}\n\nO estado anterior é guardado no diário e pode ser desfeito aqui ou na página Aplicar." +
                   (level == BrokerLevel.Admin ? "\n\nVai pedir permissão de administrador (UAC) e criar um ponto de restauração." : "");
        await RunAsync(async () =>
        {
            var r = await Host.ApplyAsync("Corrigir ponto suspeito", text, [new ActionRequest(actionId + "@1", null, args)], level);
            AfterWrite(r, level);
            if (r is { Ok: true }) await LoadAsync();
        });
    }

    private void AfterWrite(BrokerCallResult? r, BrokerLevel level)
    {
        if (r is null) return;
        Message = r.Message;
        if (r.Response is { Ok: true, RunId: { } run })
        {
            _lastRun = run;
            _lastLevel = level;
            OnPropertyChanged(nameof(CanUndo));
        }
    }

    private async Task UndoAsync()
    {
        if (Host is null || _lastRun is not { } run) return;
        await RunAsync(async () =>
        {
            var r = await Host.RevertAsync(run, _lastLevel);
            if (r is null) return;
            Message = r.Message;
            if (r.Response?.FullyRestored == true) { _lastRun = null; OnPropertyChanged(nameof(CanUndo)); }
            await LoadAsync();
            if (Orphans.Count > 0) await InspectCoreAsync();
        });
    }

    // ------------------------------------------------------------------ inspetor de registro

    private async Task InspectAsync()
    {
        Inspecting = true;
        try { await InspectCoreAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { InspectorStatus = $"Não foi possível inspecionar: {ex.Message}"; }
        finally { Inspecting = false; }
    }

    private async Task InspectCoreAsync()
    {
        InspectorStatus = "Inspecionando…";
        var list = await Task.Run(() =>
        {
            var reg = new WindowsSecurityRegistry();
            var files = new SystemFileProbe();
            return RegistryInspector.Scan(reg, files, RegScope.Machine).Concat(RegistryInspector.Scan(reg, files, RegScope.User)).ToList();
        });
        Orphans.Clear();
        foreach (var o in list.OrderByDescending(o => o.Removable).ThenBy(o => o.Category, StringComparer.Ordinal).ThenBy(o => o.Display, StringComparer.OrdinalIgnoreCase))
            Orphans.Add(new OrphanRow(o));
        InspectorStatus = list.Count == 0
            ? "Nenhuma sobra encontrada pelas regras (conservadoras) do inspetor."
            : $"{list.Count(o => o.Removable)} sobra(s) que podem ser removidas com cópia e {list.Count(o => !o.Removable)} só informativa(s). Remover não deixa o PC mais rápido.";
        OnPropertyChanged(nameof(OrphanCount));
    }

    private async Task RemoveOrphansAsync()
    {
        if (Host is null) return;
        var selected = Orphans.Where(o => o.IsSelected && o.Removable).ToList();
        if (selected.Count == 0) return;
        await RunAsync(async () =>
        {
            foreach (var group in selected.GroupBy(o => o.Orphan.Scope))
            {
                var items = group.Take(MaxSelection).ToList();
                var machine = group.Key == RegScope.Machine;
                var actionId = machine ? "security.registry.orphans.machine" : "security.registry.orphans.user";
                var level = machine ? BrokerLevel.Admin : BrokerLevel.User;
                var text = $"Remover {items.Count} sobra(s) de registro {(machine ? "de todo o PC" : "do seu usuário")}?\n\n" +
                           string.Join("\n", items.Take(8).Select(i => "• " + i.Display)) + (items.Count > 8 ? $"\n• … e mais {items.Count - 8}" : "") +
                           "\n\nIsso NÃO deixa o PC mais rápido; só arruma listas como Programas e Recursos. Cada entrada é copiada por inteiro antes (todas as subchaves, tipos e dados) e pode ser recriada com Desfazer." +
                           (group.Count() > MaxSelection ? $"\n\nLimite de {MaxSelection} por vez: as demais ficam para a próxima." : "") +
                           (machine ? "\n\nVai pedir permissão de administrador (UAC) e criar um ponto de restauração." : "");
                var args = new Dictionary<string, string> { ["selection"] = string.Join(",", items.Select(i => i.Orphan.Selection)) };
                var r = await Host.ApplyAsync("Remover sobras de registro", text, [new ActionRequest(actionId + "@1", null, args)], level);
                AfterWrite(r, level);
            }
            await InspectCoreAsync();
        });
    }
}
