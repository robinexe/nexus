using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Nexus.Broker;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Memory.Problems;
using Nexus.Measurement;
using Nexus.SystemTools.Crash;

namespace Nexus.Desktop.ViewModels.Simple;

/// <summary>Um jogo que travou, com a opção de guardar detalhes dos próximos travamentos (minidespejo, administrador).</summary>
public sealed record CrashGameRow(string Name, string Line, OneClickAction? KeepDumps)
{
    public bool HasKeepDumps => KeepDumps is not null;
}

/// <summary>
/// "O jogo fechou sozinho?" (tela Problemas): "Juntar informações do travamento" lê o que o Windows registrou (eventos 1000/1001,
/// 4101 e relatórios do WER, só leitura), mostra um resumo em palavras simples e salva um ZIP onde o usuário escolher (Documentos
/// por padrão) — sem despejos de memória nem arquivos pessoais. Para cada jogo que caiu, oferece guardar um minidespejo dos
/// próximos travamentos (ação do catálogo pelo aplicador, administrador, com Desfazer).
/// </summary>
public sealed class CrashReportPanel : ObservableObject
{
    public const string LocalDumpsAction = "system.crash.localdumps@1";

    private readonly Func<IModuleHost?> _host;
    private CrashData _data = CrashData.Empty;
    private CrashDigest? _digest;
    private bool _reading;
    private string _headline = "";
    private string _message = "";
    private string? _savedPath;
    private IReadOnlyList<string> _facts = [];
    private IReadOnlyList<string> _advice = [];
    private IReadOnlyList<string> _notes = [];
    private IReadOnlyList<CrashGameRow> _games = [];
    private HashSet<string> _knownExes = new(StringComparer.OrdinalIgnoreCase);

    public CrashReportPanel(Func<IModuleHost?> host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ReadCommand = new RelayCommand(() => _ = ReadAsync(), () => !_reading);
        SaveCommand = new RelayCommand(Save, () => !_reading && _digest is not null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _savedPath is not null);
    }

    public RelayCommand ReadCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public bool IsReading { get => _reading; private set { if (Set(ref _reading, value)) OnPropertyChanged(nameof(CanRead)); } }
    public bool CanRead => !_reading;
    public bool HasResult => _digest is not null;
    /// <summary>"O jogo X fechou 3 vezes esta semana; o driver de vídeo reiniciou 2 vezes → atualize o driver de vídeo."</summary>
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public IReadOnlyList<string> Facts { get => _facts; private set => Set(ref _facts, value); }
    public IReadOnlyList<string> Advice { get => _advice; private set { if (Set(ref _advice, value)) OnPropertyChanged(nameof(HasAdvice)); } }
    public bool HasAdvice => _advice.Count > 0;
    public IReadOnlyList<string> Notes { get => _notes; private set { if (Set(ref _notes, value)) OnPropertyChanged(nameof(HasNotes)); } }
    public bool HasNotes => _notes.Count > 0;
    public IReadOnlyList<CrashGameRow> Games { get => _games; private set { if (Set(ref _games, value)) OnPropertyChanged(nameof(HasGames)); } }
    public bool HasGames => _games.Count > 0;
    public string Message { get => _message; private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;
    public bool HasSaved => _savedPath is not null;

    /// <summary>É um jogo? Lista embutida do NEXUS, jogos achados nas lojas (tela Jogos) ou caminho dentro de uma biblioteca de jogos.</summary>
    internal bool IsGame(string app, string? path) => IsGame(app, path, _knownExes);

    internal static bool IsGame(string app, string? path, IReadOnlySet<string> knownExes)
    {
        if (string.IsNullOrWhiteSpace(app) || GameLibrary.IsHelperExe(app)) return false;
        if (knownExes.Contains(app) || GameLibrary.BuiltIn.Any(g => string.Equals(g.ExeName, app, StringComparison.OrdinalIgnoreCase))) return true;
        return ProcessProtection.LooksLikeGame(path);
    }

    internal static string GameName(string exe) =>
        GameLibrary.BuiltIn.FirstOrDefault(g => string.Equals(g.ExeName, exe, StringComparison.OrdinalIgnoreCase))?.Name ?? GameMatcher.Pretty(exe);

    private async Task ReadAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsReading = true;
        Message = "";
        try
        {
            if (_host()?.FindPage(typeof(SimpleGamesPage)) is SimpleGamesPage games)
                _knownExes = new HashSet<string>(games.Games.Select(g => g.Exe), StringComparer.OrdinalIgnoreCase);
            _data = await Task.Run(() => WindowsCrashReader.Read());
            var digest = CrashDigestBuilder.Build(_data.Crashes, _data.WerEvents, _data.DisplayResets, IsGame, DateTimeOffset.UtcNow);
            Show(digest);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = Loc.F("Crash.ReadFailed", ex.Message); }
        finally
        {
            IsReading = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    internal void Show(CrashDigest digest)
    {
        _digest = digest;
        var (headline, facts, advice) = Texts(digest);
        Headline = headline;
        Facts = facts;
        Advice = advice;
        Notes = _data.Notes;
        Games = [.. digest.Games.Take(3).Select(g =>
        {
            var last = g.Last.ToLocalTime().ToString("g", Loc.Culture);
            return new CrashGameRow(GameName(g.Exe), g.Count == 1 ? Loc.F("Crash.GameLine1", g.Count, last) : Loc.F("Crash.GameLineN", g.Count, last), KeepDumpsFor(g.Exe));
        })];
        OnPropertyChanged(nameof(HasResult));
    }

    /// <summary>Frases do resumo (pura sobre o resumo; testada pelo SimpleModeTests/UtilitiesTests via a lógica do resumo).</summary>
    internal static (string Headline, IReadOnlyList<string> Facts, IReadOnlyList<string> Advice) Texts(CrashDigest d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var facts = new List<string>();
        foreach (var g in d.Games.Take(3))
            facts.Add(g.Count == 1 ? Loc.F("Crash.GameClosed1", GameName(g.Exe), g.Count) : Loc.F("Crash.GameClosedN", GameName(g.Exe), g.Count));
        if (d.Games.Count > 3) facts.Add(Loc.F("Crash.MoreGames", d.Games.Count - 3));
        if (d.GameHangs > 0) facts.Add(d.GameHangs == 1 ? Loc.T("Crash.Hangs1") : Loc.F("Crash.HangsN", d.GameHangs));
        if (d.VideoResets > 0) facts.Add(d.VideoResets == 1 ? Loc.T("Crash.VideoReset1") : Loc.F("Crash.VideoResetN", d.VideoResets));
        var advice = d.Advice.Select(a => a switch
        {
            CrashAdvice.UpdateGpuDriver => Loc.T("Crash.Advice.UpdateGpuDriver"),
            CrashAdvice.VerifyGameFiles => Loc.T("Crash.Advice.VerifyGameFiles"),
            CrashAdvice.CheckOverclockAndTemps => Loc.T("Crash.Advice.CheckOverclockAndTemps"),
            _ => Loc.T("Crash.Advice.TestMemory"),
        }).ToList();
        if (d.Nothing) return (Loc.T("Crash.Nothing"), facts, advice);
        var headline = string.Join("; ", facts.Take(2));
        if (advice.Count > 0) headline += " → " + advice[0];
        return (headline, facts, advice);
    }

    private OneClickAction? KeepDumpsFor(string exe)
    {
        if (!LocalDumpsHandler.IsValidExe(exe)) return null;
        var name = GameName(exe);
        return new OneClickAction(Loc.T("Crash.KeepDumps"), () => _host() is { } h ? new QuickApplier(h) : null,
            q => q.ApplyAsync(Loc.T("Crash.KeepDumpsTitle"), Loc.F("Crash.KeepDumpsConfirm", name),
                [(new ActionRequest(LocalDumpsAction, null, new Dictionary<string, string> { ["gameExe"] = exe }), BrokerLevel.Admin)]));
    }

    private void Save()
    {
        if (_digest is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Crash.SaveTitle"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = "NEXUS-travamentos-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip",
            Filter = Loc.T("Crash.ZipFilter"),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var summary = SummaryText(_digest);
            var system = SystemText(_host()?.Facts);
            using (var file = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None))
                CrashBundle.Write(file, _data, IsGame, summary, system, DateTimeOffset.UtcNow);
            _savedPath = dialog.FileName;
            OnPropertyChanged(nameof(HasSaved));
            Message = Loc.F("Crash.Saved", dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Message = Loc.F("Crash.SaveFailed", ex.Message); }
    }

    private static string SummaryText(CrashDigest d)
    {
        var (headline, facts, advice) = Texts(d);
        var sb = new StringBuilder();
        sb.Append(headline).Append("\r\n\r\n");
        foreach (var f in facts) sb.Append("- ").Append(f).Append("\r\n");
        if (advice.Count > 0)
        {
            sb.Append("\r\n").Append(Loc.T("Crash.WhatToDo")).Append("\r\n");
            foreach (var a in advice) sb.Append("- ").Append(a).Append("\r\n");
        }
        foreach (var g in d.Games)
            sb.Append("\r\n").Append(g.Exe).Append(": ").Append(g.Count.ToString(CultureInfo.InvariantCulture))
              .Append("x; module ").Append(g.TopModule ?? "?").Append("; exception ").Append(g.TopException ?? "?");
        return sb.ToString();
    }

    /// <summary>Resumo do PC para quem for ajudar: peças e versões. Sem nome do computador, conta, IPs ou programas instalados.</summary>
    internal static string SystemText(SystemFacts? f)
    {
        if (f is null) return "System summary unavailable (the PC reading had not finished).";
        var sb = new StringBuilder();
        void Line(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) sb.Append(label).Append(": ").Append(value).Append("\r\n"); }
        Line("Windows", (f.WindowsEdition ?? "") + (f.WindowsBuild is { } b ? " build " + b.ToString(CultureInfo.InvariantCulture) : ""));
        Line("PC", string.Join(" ", new[] { f.SystemManufacturer, f.SystemModel }.Where(s => !string.IsNullOrWhiteSpace(s))));
        Line("CPU", f.CpuModel + (f.PhysicalCores is { } c ? $" ({c.ToString(CultureInfo.InvariantCulture)}c/{f.LogicalProcessors?.ToString(CultureInfo.InvariantCulture) ?? "?"}t)" : ""));
        foreach (var g in f.Gpus)
            Line("GPU", g.Name + (g.DriverVersion is null ? "" : " — driver " + g.DriverVersion) + (g.DriverDate is null ? "" : " (" + g.DriverDate + ")"));
        Line("RAM", Units.Bytes(f.TotalMemoryBytes) + (f.MemoryModules.Count > 0 ? $" — {f.MemoryModules.Count.ToString(CultureInfo.InvariantCulture)} module(s)" +
            (f.MemoryModules[0].ConfiguredSpeedMts is { } s ? $" @ {s.ToString(CultureInfo.InvariantCulture)} MT/s" : "") : ""));
        Line("System disk", f.SystemDiskMedia + (f.SystemDiskBus is null ? "" : " / " + f.SystemDiskBus));
        Line("Power plan", f.ActivePowerSchemeName);
        Line("Virtual machine", f.IsVirtualMachine?.ToString());
        return sb.ToString();
    }

    private void OpenFolder()
    {
        if (_savedPath is null || !File.Exists(_savedPath)) return;
        try { using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_savedPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = ex.Message; }
    }
}
