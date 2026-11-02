using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Nexus.Broker;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Memory;
using UiTone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>Um slot (ou posição soldada) no diagrama de canais.</summary>
public sealed class MemSlotRow
{
    public required string Title { get; init; }
    public required bool Occupied { get; init; }
    public string SizeText { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Tip { get; init; } = "";
    /// <summary>Slot livre de verdade (não soldado): "encaixe aqui".</summary>
    public bool CanAdd => !Occupied;
}

/// <summary>Um canal com seus slots.</summary>
public sealed class MemChannelGroup
{
    public required string Name { get; init; }
    public required bool HasMemory { get; init; }
    public IReadOnlyList<MemSlotRow> Slots { get; init; } = [];
}

/// <summary>Par rótulo/valor (ficha do módulo, estado do Windows).</summary>
public sealed record MemField(string Label, string Value);

/// <summary>Ficha completa de um módulo instalado.</summary>
public sealed class MemModuleRow
{
    public required string Title { get; init; }
    public required string Decoded { get; init; }
    public required string Confidence { get; init; }
    public IReadOnlyList<MemField> Fields { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Barra de banda: medido vs teórico.</summary>
public sealed class MemBarRow
{
    public required string Label { get; init; }
    public required string ValueText { get; init; }
    public required double Fraction { get; init; }
    public string Detail { get; init; } = "";
    /// <summary>A barra do teórico (cinza, referência).</summary>
    public bool IsTheory => Label == "Teórico";
}

/// <summary>Fonte citada (abre no navegador).</summary>
public sealed class MemSourceRow
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required ICommand OpenCommand { get; init; }
}

/// <summary>Achado do diagnóstico, com ação reversível opcional.</summary>
public sealed class MemFindingRow : ObservableObject
{
    private string _status = "";
    private Guid? _runId;

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Severity { get; init; }
    public required string Evidence { get; init; }
    public required string Recommendation { get; init; }
    public IReadOnlyList<string> Steps { get; init; } = [];
    public IReadOnlyList<MemSourceRow> Sources { get; init; } = [];
    public string? ActionId { get; init; }
    /// <summary>Só o arquivo de paginação tem botão aqui (o texto da confirmação é dele).</summary>
    public bool HasAction => ActionId == MemoryDiagnosis.PageFileActionId;
    public bool HasSteps => Steps.Count > 0;
    public string SeverityText => Severity switch { "bad" => "Problema", "warn" => "Atenção", "good" => "Tudo certo", _ => "Informação" };
    public UiTone ToneKind => Severity switch { "bad" => UiTone.Bad, "warn" => UiTone.Warn, "good" => UiTone.Good, _ => UiTone.Info };
    public string IconKey => Severity switch { "bad" => "Icon.Error", "warn" => "Icon.Warning", "good" => "Icon.Check", _ => "Icon.Info" };
    public bool HasDetails => Steps.Count > 0 || Sources.Count > 0;
    public ICommand? ApplyCommand { get; set; }
    public ICommand? RevertCommand { get; set; }
    public string Status { get => _status; set => Set(ref _status, value); }
    public Guid? RunId { get => _runId; set { if (Set(ref _runId, value)) OnPropertyChanged(nameof(CanRevert)); } }
    public bool CanRevert => RunId is not null;
}

/// <summary>Medição anterior salva.</summary>
public sealed record MemRunRow(string When, string Configuration, string Read, string Write, string Copy, string Latency);

/// <summary>
/// Laboratório da memória RAM (somente leitura, exceto a ação reversível do arquivo de paginação, que vai pelo aplicador).
/// Lê o SMBIOS (módulos, slots, canais), decodifica números de peça, mede banda e latência sem driver e explica o que achou.
/// </summary>
public sealed class MemoryLabPage : ModulePage
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private const ulong GiB = 1024UL * 1024 * 1024;

    private readonly MemoryLabStore _store = new();
    private Action? _cancel;
    private SystemFacts? _facts;
    private MemoryAnalysis? _analysis;
    private MemoryState? _state;
    private BandwidthResult? _bandwidth;
    private LatencyResult? _latency;
    private ChannelCheck? _check;
    private bool _loaded, _measuring, _stateLoading;
    private string _headline = "Lendo a memória…", _headlineTone = "info", _subline = "", _capacityText = "", _channelText = "";
    private string _verdict = "", _theoryText = "", _latencyText = "", _latencyDetail = "", _progress = "", _compareText = "", _stateNote = "";
    private string _gamingHeadline = "", _gamingDetail = "";
    private bool _attempted;

    public MemoryLabPage()
    {
        MeasureCommand = new RelayCommand(() => _ = MeasureAsync(), () => !_measuring && _analysis is not null);
        CancelCommand = new RelayCommand(() => _cancel?.Invoke(), () => _measuring);
        RefreshStateCommand = new RelayCommand(() => _ = LoadStateAsync(), () => !_stateLoading);
    }

    public override string Title => "Memória RAM";
    public override string Group => Loc.T("Nav.Laboratorio");
    public override string Subtitle => "Engenharia reversa da sua memória: módulos, canais, velocidade real e latência medida.";

    public ICommand MeasureCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshStateCommand { get; }

    public ObservableCollection<MemChannelGroup> Channels { get; } = [];
    public ObservableCollection<MemModuleRow> Modules { get; } = [];
    public ObservableCollection<MemBarRow> Bars { get; } = [];
    public ObservableCollection<MemFindingRow> Findings { get; } = [];
    public ObservableCollection<MemField> WindowsState { get; } = [];
    public ObservableCollection<MemRunRow> Runs { get; } = [];
    public ObservableCollection<MemSourceRow> GamingSources { get; } = [];

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    /// <summary>"bad" (single channel), "good" (multicanal) ou "info".</summary>
    public string HeadlineTone { get => _headlineTone; private set => Set(ref _headlineTone, value); }
    public string Subline { get => _subline; private set => Set(ref _subline, value); }
    public string CapacityText { get => _capacityText; private set => Set(ref _capacityText, value); }
    public string ChannelText { get => _channelText; private set => Set(ref _channelText, value); }
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string TheoryText { get => _theoryText; private set => Set(ref _theoryText, value); }
    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }
    public string LatencyDetail { get => _latencyDetail; private set => Set(ref _latencyDetail, value); }
    public string ProgressText { get => _progress; private set => Set(ref _progress, value); }
    public string CompareText { get => _compareText; private set => Set(ref _compareText, value); }
    public string StateNote { get => _stateNote; private set => Set(ref _stateNote, value); }
    public string GamingHeadline { get => _gamingHeadline; private set => Set(ref _gamingHeadline, value); }
    public string GamingDetail { get => _gamingDetail; private set => Set(ref _gamingDetail, value); }
    public bool Measuring
    {
        get => _measuring;
        private set
        {
            if (!Set(ref _measuring, value)) return;
            OnPropertyChanged(nameof(CancelText));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool HasBars => Bars.Count > 0;
    public bool HasRuns => Runs.Count > 0;

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Verdadeiro até a primeira leitura do SMBIOS terminar (esqueleto no destaque).</summary>
    public bool IsLoading => !_attempted;
    /// <summary>Memória instalada (bytes; NaN = ainda não lida).</summary>
    public double InstalledBytes => _analysis is { } a ? a.Inventory.InstalledBytes : double.NaN;
    public bool IsSingleChannel => _analysis?.Layout.Mode == ChannelMode.SingleChannel;
    /// <summary>"—" no destaque quando a leitura terminou sem tamanho.</summary>
    public string HeroFallback => _attempted && !double.IsFinite(InstalledBytes) ? "—" : "";
    /// <summary>Há um slot livre (não soldado) para um segundo pente.</summary>
    public bool HasFreeSlot => _analysis?.Inventory.Empty.Any(d => !d.LooksSoldered) == true;
    public bool ShowUpgradeSteps => IsSingleChannel && HasFreeSlot;
    public UiTone ChannelTone => _analysis?.Layout.Mode switch
    {
        ChannelMode.SingleChannel => UiTone.Warn,
        ChannelMode.DualChannel or ChannelMode.QuadChannel or ChannelMode.MultiChannel => UiTone.Good,
        _ => UiTone.Info,
    };
    public string ChannelModeText => _analysis?.Layout.Mode switch
    {
        ChannelMode.SingleChannel => "Canal único",
        ChannelMode.DualChannel => "Dois canais (dual channel)",
        ChannelMode.QuadChannel => "Quatro canais",
        ChannelMode.MultiChannel => "Vários canais",
        null => "",
        _ => "Canais não identificados",
    };
    /// <summary>"1 de 2 slots em uso".</summary>
    public string SlotsText => _analysis is { } a
        ? $"{a.Inventory.Populated.Count} de {a.Inventory.Devices.Count} {(a.Inventory.Devices.Count == 1 ? "posição" : "posições")} em uso"
        : "";
    /// <summary>Banda teórica com a configuração de agora (GB/s; 0 = velocidade não informada).</summary>
    public double TheoryGbps => _analysis?.TheoreticalGbps ?? 0;
    /// <summary>Com um segundo pente igual (dois canais): o dobro do canal único.</summary>
    public double DualTheoryGbps => IsSingleChannel ? TheoryGbps * 2 : TheoryGbps;
    public bool ShowDualDemo => IsSingleChannel && TheoryGbps > 0;
    /// <summary>Leitura medida (GB/s; NaN = ainda não medido).</summary>
    public double ReadGbps => _bandwidth?.ReadGbps is { } r && r > 0 ? r : double.NaN;
    public double ReadPercentOfTheory => _bandwidth is { } b && TheoryGbps > 0 ? Math.Clamp(b.ReadGbps / TheoryGbps * 100, 0, 100) : double.NaN;
    public double LatencyNs => _latency?.RandomNs is { } n && n > 0 ? n : double.NaN;
    /// <summary>Estado do Windows: % em uso e % do limite de memória comprometida (NaN = não lido).</summary>
    public double UsedPercent => _state is { TotalPhysicalBytes: { } t, AvailablePhysicalBytes: { } av } && t > 0 ? (t - av) * 100.0 / t : double.NaN;
    public double CommitPercent => _state is { CommitTotalBytes: { } ct, CommitLimitBytes: { } cl } && cl > 0 ? ct * 100.0 / cl : double.NaN;
    public double StandbyBytes => _state?.StandbyBytes is { } sb ? sb : double.NaN;
    public bool HasState => _state is not null;
    public string CancelText => Measuring ? "Cancelar medição" : "";

    private void NotifyNumbers()
    {
        foreach (var name in new[]
                 {
                     nameof(IsLoading), nameof(InstalledBytes), nameof(HeroFallback), nameof(IsSingleChannel), nameof(HasFreeSlot), nameof(ShowUpgradeSteps),
                     nameof(ChannelTone), nameof(ChannelModeText), nameof(SlotsText), nameof(TheoryGbps), nameof(DualTheoryGbps),
                     nameof(ShowDualDemo), nameof(ReadGbps), nameof(ReadPercentOfTheory), nameof(LatencyNs), nameof(UsedPercent),
                     nameof(CommitPercent), nameof(StandbyBytes), nameof(HasState),
                 })
            OnPropertyChanged(name);
    }

    public static string MythText =>
        "\"Limpador de RAM\" não acelera nada. O Windows usa a memória livre como cache (lista de espera) de propósito: o que está " +
        "ali abre mais rápido e é devolvido na hora a quem precisar. Esvaziar à força joga esse cache fora, faz os programas lerem " +
        "de novo do disco e empurra páginas para o arquivo de paginação. Por isso o NEXUS não tem esse botão.";

    public static string NoDriverText =>
        "Timings detalhados (tCL, tRCD, tRP, tRAS…) e a leitura direta do chip SPD do módulo exigem acesso ao barramento SMBus da placa, " +
        "que só um driver de kernel tem. O NEXUS não instala driver: mostra o que o firmware informa (SMBIOS), o que o número de peça " +
        "revela e o que a medição comprova. Para os timings exatos, use o CPU-Z ou o utilitário do fabricante da placa.";

    public override async Task OnFactsReadyAsync(SystemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        _facts = facts;
        await LoadAsync();
    }

    public override async Task OnShownAsync()
    {
        if (!_loaded) await LoadAsync();
        if (_state is null && !_stateLoading) await LoadStateAsync();
        LoadRuns();
    }

    // ------------------------------------------------------------------ leitura

    private async Task LoadAsync()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) { Headline = "Disponível só no Windows"; return; }
            var gpus = _facts?.Gpus ?? [];
            var analysis = await Task.Run(() => MemoryLabAnalyzer.Analyze(WindowsMemoryReader.ReadSmbios(), gpus));
            _analysis = analysis;
            _loaded = true;
            ShowAnalysis();
            Rebuild();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Headline = "Não foi possível ler a memória";
            Message = $"Leitura do SMBIOS falhou: {ex.Message}";
        }
        finally
        {
            _attempted = true;
            NotifyNumbers();
        }
    }

    private async Task LoadStateAsync()
    {
        if (!OperatingSystem.IsWindows() || _stateLoading) return;
        _stateLoading = true;
        StateNote = "Lendo o estado da memória do Windows…";
        try
        {
            var sysMain = _facts?.Services.FirstOrDefault(s => string.Equals(s.Name, "SysMain", StringComparison.OrdinalIgnoreCase));
            _state = await Task.Run(() => WindowsMemoryReader.ReadState(sysMain?.State, sysMain?.StartType));
            ShowState();
            NotifyNumbers();
            StateNote = _state.Problems.Count == 0 ? "" : "Não lido: " + string.Join("; ", _state.Problems.Select(p => $"{p.Key} ({p.Value})"));
            Rebuild();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { StateNote = $"Não foi possível ler o estado: {ex.Message}"; }
        finally { _stateLoading = false; CommandManager.InvalidateRequerySuggested(); }
    }

    private void ShowAnalysis()
    {
        var a = _analysis!;
        Headline = a.Headline;
        HeadlineTone = a.Layout.Mode switch
        {
            ChannelMode.SingleChannel => "bad",
            ChannelMode.DualChannel or ChannelMode.QuadChannel or ChannelMode.MultiChannel => "good",
            _ => "info",
        };
        var machine = string.Join(" ", new[] { a.Machine.SystemMaker, a.Machine.Model }.Where(s => s is not null));
        var kind = a.Machine.Portable switch { true => "notebook", false => "PC de mesa", _ => null };
        Subline = string.Join(" · ", new[] { machine.Length > 0 ? machine : null, kind, a.Layout.Explanation }.Where(s => s is not null));

        var inv = a.Inventory;
        var free = inv.Empty.Count(d => !d.LooksSoldered);
        var soldered = inv.Populated.Count(d => d.LooksSoldered);
        var parts = new List<string> { $"{Gb(inv.InstalledBytes)} instalados" };
        if (inv.MaxCapacityBytes is { } max) parts.Add($"a placa aceita até {Gb(max)}");
        parts.Add($"{inv.Devices.Count} posiç{(inv.Devices.Count == 1 ? "ão" : "ões")}");
        parts.Add(free == 0 ? "nenhum slot livre" : $"{free} slot{(free > 1 ? "s" : "")} livre{(free > 1 ? "s" : "")}");
        if (soldered > 0) parts.Add($"{soldered} soldad{(soldered > 1 ? "as" : "a")} na placa");
        CapacityText = string.Join(" · ", parts);

        Channels.Clear();
        var groups = a.Layout.Slots.GroupBy(s => s.Channel ?? "?").OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var slots = g.Select(s =>
            {
                var d = s.Device;
                var part = d.Populated ? PartNumberDecoder.Decode(d.PartNumber) : null;
                return new MemSlotRow
                {
                    Title = d.Locator ?? d.BankLocator ?? "sem nome",
                    Occupied = d.Populated,
                    SizeText = d.Populated ? Gb(d.SizeBytes) : "vazio",
                    Detail = d.Populated ? $"{d.Type} {(d.LooksSoldered ? "soldada" : FormName(d.FormFactor))}".Trim() : "slot livre",
                    Tip = $"{d.Locator} / {d.BankLocator}" + (part is { Decoded: true } ? "\n" + part.Summary : ""),
                };
            }).ToList();
            Channels.Add(new MemChannelGroup { Name = g.Key == "?" ? "Canal não informado" : $"Canal {g.Key}", HasMemory = slots.Any(s => s.Occupied), Slots = slots });
        }
        ChannelText = a.Layout.FromNames
            ? "Canais lidos dos nomes que o firmware dá aos slots."
            : "O firmware não informa o canal no nome dos slots: o diagrama é uma estimativa e a medição de banda confirma.";

        Modules.Clear();
        for (var i = 0; i < inv.Populated.Count; i++)
        {
            var d = inv.Populated[i];
            var part = a.Parts[i];
            var (vendor, how) = JedecVendors.Resolve(d.Manufacturer, d.ModuleManufacturerId);
            var fields = new List<MemField>
            {
                new("Slot", $"{d.Locator ?? "?"}{(d.BankLocator is null ? "" : $" · {d.BankLocator}")}"),
                new("Fabricante", vendor is null ? "desconhecido" : $"{vendor} ({how})"),
                new("Número de peça", d.PartNumber ?? "não informado"),
                new("Série", d.SerialNumber ?? "não informada"),
                new("Tamanho", Gb(d.SizeBytes)),
                new("Tipo", d.Type ?? "não informado"),
                new("Forma", d.LooksSoldered ? "soldada na placa" : FormName(d.FormFactor) + (d.FormFactorRaw is null ? "" : $" ({d.FormFactorRaw})")),
                new("Velocidade nominal", d.SpeedMts is { } s ? $"{s} MT/s" : "não informada"),
                new("Velocidade configurada", d.ConfiguredSpeedMts is { } c ? $"{c} MT/s" : "não informada"),
                new("Ranks", d.Rank is { } r ? r.ToString(Pt) : "não informado"),
                new("Largura de dados", d.DataWidthBits is { } w ? $"{w} bits{(d.TotalWidthBits is { } tw && tw > w ? $" ({tw} com ECC)" : "")}" : "não informada"),
                new("Tensão (mín/máx/config.)", Volts(d.MinVoltageMv, d.MaxVoltageMv, d.ConfiguredVoltageMv)),
            };
            if (d.Registered) fields.Add(new MemField("Buffer", "registrada (RDIMM)"));
            var notes = part.Notes.ToList();
            if (part.KitModules is > 1) notes.Add($"Vendido em kit de {part.KitModules} módulos ({Gb(part.KitTotalBytes)} no total).");
            if (part.Profile is "XMP" or "EXPO" or "XMP/EXPO" && part.SpeedMts is { } adv)
                notes.Add($"A velocidade anunciada ({adv} MT/s) depende do perfil {part.Profile} no BIOS; sem ele, o módulo roda na velocidade JEDEC.");
            Modules.Add(new MemModuleRow
            {
                Title = $"{Gb(d.SizeBytes)} {d.Type} · {d.Locator ?? "slot"}",
                Decoded = part.Summary,
                Confidence = part.Confidence switch
                {
                    DecodeConfidence.High => "Decodificação: confiança alta (padrão público do fabricante)",
                    DecodeConfidence.Medium => "Decodificação: confiança média (parte do número segue padrão conhecido)",
                    DecodeConfidence.Low => "Decodificação: confiança baixa (só a geração)",
                    _ => "Número de peça desconhecido: nada foi deduzido dele",
                },
                Fields = fields,
                Notes = notes,
            });
        }

        var theory = a.TheoreticalGbps;
        TheoryText = a.ConfiguredMts is { } mts && theory > 0
            ? string.Create(Pt, $"Teórico: {mts} MT/s × {a.Layout.BusWidthBits / 8} bytes ({a.Layout.BusWidthBits} bits, {a.Layout.ChannelCount} canal{(a.Layout.ChannelCount == 1 ? "" : "is")}) = {theory:0.0} GB/s.")
            : "Velocidade não informada pelo firmware: sem valor teórico para comparar.";
        var gaming = MemoryDiagnosis.Gaming(a.Layout, a.Machine);
        GamingHeadline = gaming.Headline;
        GamingDetail = gaming.Detail;
        GamingSources.Clear();
        foreach (var s in gaming.Sources) GamingSources.Add(Source(s));
        Verdict = BandwidthTheory.Verdict(a.Layout, _check ?? BandwidthTheory.Check(0, a.ConfiguredMts ?? 0, a.WidthPerChannelBits, a.Layout.BusWidthBits));
    }

    private void ShowState()
    {
        var s = _state!;
        WindowsState.Clear();
        WindowsState.Add(new MemField("Em uso agora", s.TotalPhysicalBytes is { } t && s.AvailablePhysicalBytes is { } av ? $"{Gb(t - av)} de {Gb(t)} ({(t - av) * 100.0 / t:0}%)" : "?"));
        WindowsState.Add(new MemField("Memória comprometida", s.CommitTotalBytes is { } ct && s.CommitLimitBytes is { } cl ? $"{Gb(ct)} de {Gb(cl)} (limite = RAM + paginação)" : "?"));
        WindowsState.Add(new MemField("Pico desde que ligou", s.CommitPeakBytes is { } pk ? $"{Gb(pk)}{(s.Uptime is { } up ? $" (ligado há {Uptime(up)})" : "")}" : "?"));
        WindowsState.Add(new MemField("Cache (lista de espera)", s.StandbyBytes is { } sb ? $"{Gb(sb)} — memória em cache, devolvida na hora a quem precisar" : "?"));
        WindowsState.Add(new MemField("Faltas de página", s.PageFaultsPerSec is { } pf
            ? string.Create(Pt, $"{pf:N0}/s no total; {s.PagesInputPerSec ?? 0:N0} páginas/s lidas do disco (só estas indicam falta de RAM)")
            : "?"));
        WindowsState.Add(new MemField("Compressão de memória", s.CompressionEnabled switch { true => "ligada", false => "desligada", _ => "não lida" } + (s.CompressionSource.Length > 0 ? $" ({s.CompressionSource})" : "")));
        WindowsState.Add(new MemField("Arquivo de paginação", s.PageFile.Describe() + (s.PageFileCurrentBytes is { } pfs && pfs > 0 ? $" · tamanho atual {Gb(pfs)}" : "")));
        WindowsState.Add(new MemField("SysMain (Superfetch)", s.SysMainState is null ? "não lido" : $"{s.SysMainState}{(s.SysMainStartType is null ? "" : $", início {s.SysMainStartType}")} — veja a página Serviços"));
        if (s.EnablePrefetcher is { } ep)
            WindowsState.Add(new MemField("Prefetch", ep switch { 0 => "desligado", 1 => "só programas", 2 => "só inicialização", 3 => "programas e inicialização (padrão)", _ => ep.ToString(Pt) }));
    }

    private void Rebuild()
    {
        if (_analysis is null) return;
        var facts = new MemoryLabFacts(_analysis.Inventory, _analysis.Layout, _analysis.Parts, _analysis.Machine, _state, _bandwidth, _check, _latency);
        var previous = Findings.Where(f => f.RunId is not null).ToDictionary(f => f.Id, f => (f.RunId, f.Status));
        Findings.Clear();
        foreach (var f in MemoryDiagnosis.Evaluate(facts))
        {
            var row = new MemFindingRow
            {
                Id = f.Id, Title = f.Title, Severity = f.Severity, Evidence = f.Evidence, Recommendation = f.Recommendation,
                Steps = f.Steps, Sources = f.Sources.Select(Source).ToList(), ActionId = f.ActionId,
            };
            if (previous.TryGetValue(f.Id, out var p)) { row.RunId = p.RunId; row.Status = p.Status; }
            if (row.HasAction)
            {
                row.ApplyCommand = new RelayCommand(() => _ = RunAsync(() => ApplyAsync(row)), () => !Busy && !row.CanRevert);
                row.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertAsync(row)), () => !Busy && row.CanRevert);
            }
            Findings.Add(row);
        }
    }

    // ------------------------------------------------------------------ medição

    private async Task MeasureAsync()
    {
        if (_analysis is null || Measuring) return;
        Measuring = true;
        using var cts = new CancellationTokenSource();
        _cancel = cts.Cancel;
        var token = cts.Token;
        var progress = new Progress<string>(p => ProgressText = p);
        try
        {
            var avail = OperatingSystem.IsWindows() ? WindowsMemoryReader.AvailablePhysical() : null;
            var options = BenchmarkOptions.ForAvailableMemory(avail ?? 2 * GiB, Environment.ProcessorCount);
            ProgressText = $"Preparando {Gb((ulong)options.BufferBytes * 2)} para a banda…";
            var bw = await Task.Run(() => MemoryBenchmark.MeasureBandwidth(options, progress, token), token);
            _bandwidth = bw;
            var lat = await Task.Run(() => MemoryBenchmark.MeasureLatency(options, null, progress, token), token);
            _latency = lat;
            ShowMeasure();
            NotifyNumbers();
            SaveRun();
            ProgressText = "Medição concluída e salva para comparar depois.";
            Rebuild();
        }
        catch (OperationCanceledException) { ProgressText = "Medição cancelada."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ProgressText = $"A medição falhou: {ex.Message}"; }
        finally
        {
            _cancel = null;
            Measuring = false;
        }
    }

    private void ShowMeasure()
    {
        var a = _analysis!;
        var bw = _bandwidth!;
        var theory = a.TheoreticalGbps;
        _check = BandwidthTheory.Check(bw.ReadGbps, a.ConfiguredMts ?? 0, a.WidthPerChannelBits, a.Layout.BusWidthBits);
        Verdict = BandwidthTheory.Verdict(a.Layout, _check) + " " + _check.Text;
        var scale = Math.Max(theory, Math.Max(bw.ReadGbps, Math.Max(bw.WriteGbps, bw.CopyGbps))) * 1.05;
        Bars.Clear();
        if (theory > 0)
            Bars.Add(new MemBarRow { Label = "Teórico", ValueText = string.Create(Pt, $"{theory:0.0} GB/s"), Fraction = theory / scale, Detail = TheoryText });
        Bars.Add(Bar("Leitura", bw.ReadGbps, theory, scale, bw.ReadSamples));
        Bars.Add(Bar("Escrita", bw.WriteGbps, theory, scale, bw.WriteSamples));
        Bars.Add(Bar("Cópia", bw.CopyGbps, theory, scale, bw.CopySamples));
        OnPropertyChanged(nameof(HasBars));

        var lat = _latency!;
        LatencyText = string.Create(Pt, $"{lat.RandomNs:0.0} ns");
        LatencyDetail = string.Create(Pt,
            $"Perseguição de ponteiros aleatória num bloco de {Gb((ulong)lat.BlockBytes)} (bem maior que o cache L3): {lat.RandomNs:0.0} ns por acesso. " +
            $"Visitando página por página (quase sem falta de TLB): {lat.PageLocalNs:0.0} ns. {lat.PageNote} " +
            $"Banda: {bw.Threads} threads, blocos de {Gb((ulong)bw.BufferBytes)}, {bw.Passes} passadas (mediana), {bw.Instructions}.");
    }

    private static MemBarRow Bar(string label, double gbps, double theory, double scale, IReadOnlyList<double> samples) => new()
    {
        Label = label,
        ValueText = string.Create(Pt, $"{gbps:0.0} GB/s") + (theory > 0 ? string.Create(Pt, $" · {gbps / theory * 100:0}% do teórico") : ""),
        Fraction = scale > 0 ? Math.Clamp(gbps / scale, 0, 1) : 0,
        Detail = "Passadas: " + string.Join(" · ", samples.Select(s => s.ToString("0.0", Pt))),
    };

    private void SaveRun()
    {
        try
        {
            var a = _analysis!;
            _store.Save(new MemoryRun
            {
                TakenAt = DateTimeOffset.Now, Configuration = a.Headline, ConfiguredMts = a.ConfiguredMts, ModuleCount = a.Inventory.Populated.Count,
                ChannelMode = a.Layout.Mode.ToString(), TheoreticalGbps = a.TheoreticalGbps, Bandwidth = _bandwidth, Latency = _latency, ChannelVerdict = _check?.Text,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = $"Não foi possível salvar a medição: {ex.Message}"; }
        LoadRuns();
    }

    private void LoadRuns()
    {
        var runs = _store.LoadAll(10);
        Runs.Clear();
        foreach (var r in runs)
            Runs.Add(new MemRunRow(r.TakenAt.ToLocalTime().ToString("dd/MM HH:mm", Pt), r.Configuration,
                Num(r.Bandwidth?.ReadGbps), Num(r.Bandwidth?.WriteGbps), Num(r.Bandwidth?.CopyGbps),
                r.Latency is { } l ? l.RandomNs.ToString("0.0", Pt) + " ns" : "—"));
        OnPropertyChanged(nameof(HasRuns));
        CompareText = runs.Count >= 2 && runs[0].Bandwidth is { } now && runs[1].Bandwidth is { } before && before.ReadGbps > 0
            ? string.Create(Pt, $"Leitura: {before.ReadGbps:0.0} → {now.ReadGbps:0.0} GB/s ({(now.ReadGbps / before.ReadGbps - 1) * 100:+0;-0}%) em relação à medição de {runs[1].TakenAt.ToLocalTime():dd/MM HH:mm}.")
            : runs.Count == 1 ? "Primeira medição salva. Meça de novo depois de mudar algo (ex.: segundo módulo) para comparar." : "";
    }

    // ------------------------------------------------------------------ ação reversível (pelo aplicador)

    private async Task ApplyAsync(MemFindingRow row)
    {
        // O texto e o nível abaixo são do arquivo de paginação, a única ação desta página; outra ação precisaria do próprio texto.
        if (Host is null || row.ActionId != MemoryDiagnosis.PageFileActionId) return;
        var r = await Host.ApplyAsync("Arquivo de paginação automático",
            "O Windows volta a gerenciar o arquivo de paginação sozinho (padrão do sistema).\n\nRisco: baixo; ocupa alguns GB no disco.\n" +
            "Exige administrador e vale depois de reiniciar.\nDesfazer: volta exatamente a configuração anterior.",
            [new ActionRequest(MemoryDiagnosis.PageFileActionKey)], BrokerLevel.Admin);
        if (r is null) return;
        var outcome = r.Response?.Results is { Count: > 0 } items ? items[0].Outcome : "";
        if (outcome is "Verified" or "AlreadyConfigured")
        {
            row.RunId = outcome == "Verified" ? r.Response?.RunId : null;
            row.Status = outcome == "Verified" ? "Aplicado. Reinicie o PC para valer." : "Já estava no automático.";
        }
        else row.Status = outcome.Length == 0 ? r.Message : $"Não aplicado ({outcome}).";
        Message = row.Status;
    }

    private async Task RevertAsync(MemFindingRow row)
    {
        if (Host is null || row.RunId is not { } run) return;
        var r = await Host.RevertAsync(run, BrokerLevel.Admin);
        if (r is null) return;
        if (r.Response?.FullyRestored == true) { row.RunId = null; row.Status = "Desfeito. Reinicie o PC para valer."; }
        else row.Status = $"Não desfeito: {r.Message}";
        Message = row.Status;
    }

    // ------------------------------------------------------------------ utilidades

    private static MemSourceRow Source(MemorySource s) => new() { Title = s.Title, Url = s.Url, OpenCommand = new RelayCommand(() => OpenUrl(s.Url)) };

    /// <summary>Abre só as fontes fixas do diagnóstico (https), nunca texto vindo de fora.</summary>
    private static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static string Gb(ulong? bytes) =>
        bytes is { } b ? (b / (double)GiB).ToString(b % GiB == 0 || b > 20 * GiB ? "0" : "0.0", Pt) + " GB" : "?";

    private static string Num(double? v) => v is { } x ? x.ToString("0.0", Pt) : "—";

    private static string Volts(int? min, int? max, int? conf)
    {
        if (min is null && max is null && conf is null) return "não informada";
        static string V(int? mv) => mv is { } v ? (v / 1000.0).ToString("0.00", Pt) + " V" : "?";
        return $"{V(min)} / {V(max)} / {V(conf)}";
    }

    private static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? string.Create(Pt, $"{(int)t.TotalDays} d {t.Hours} h") : string.Create(Pt, $"{t.Hours} h {t.Minutes} min");

    private static string FormName(MemoryFormFactor f) => f switch
    {
        MemoryFormFactor.Dimm => "DIMM (PC de mesa)",
        MemoryFormFactor.SoDimm => "SODIMM (notebook)",
        MemoryFormFactor.Soldered => "soldada",
        MemoryFormFactor.Camm => "CAMM",
        MemoryFormFactor.Other => "outro",
        _ => "não informada",
    };
}
