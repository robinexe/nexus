using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.Automation;

/// <summary>
/// Handler de "Iniciar com o Windows" (ação <c>automation.start-with-windows</c>): o valor <c>NEXUS</c> em
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> com <c>"&lt;pasta&gt;\Nexus.Desktop.exe" --minimized</c>.
/// O caminho é dinâmico (a pasta onde o NEXUS está), por isso um handler próprio: o valor gravado é sempre montado aqui, a partir
/// de um caminho validado (local, sem aspas/%/controle), nunca de texto do cliente.
/// Estado: "OFF" (sem valor), "ON" (valor do NEXUS apontando para este Nexus.Desktop.exe), "STALE" (do NEXUS, outra pasta) ou
/// "FOREIGN" (outro conteúdo com o nome NEXUS: nunca tocado). Detail["value"] guarda o texto anterior para a restauração.
/// </summary>
public sealed partial class AutomationStartWithWindowsHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "AutomationStartWithWindows";
    public const string StateType = "StartWithWindows";
    public const string On = "ON";
    public const string Off = "OFF";
    public const string Stale = "STALE";
    public const string Foreign = "FOREIGN";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "NEXUS";
    public const string DesktopExecutableName = "Nexus.Desktop.exe";
    public const string MinimizedArgument = "--minimized";

    private static readonly RegistryTarget Target = new(RegHive.HKCU, RunKey, ValueName, RegView.Default);

    private readonly IRegistryBackend _registry;
    private readonly Func<string?> _desktopExecutable;
    private readonly bool _otherAccount;

    public AutomationStartWithWindowsHandler(IRegistryBackend registry, Func<string?> desktopExecutable, bool otherAccount)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _desktopExecutable = desktopExecutable ?? throw new ArgumentNullException(nameof(desktopExecutable));
        _otherAccount = otherAccount;
    }

    public string HandlerKind => Kind;

    /// <summary>Nexus.Desktop.exe ao lado do aplicador (a interface leva o aplicador junto).</summary>
    public static string? DefaultDesktopExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, DesktopExecutableName);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>Texto gravado no Run: <c>"C:\...\Nexus.Desktop.exe" --minimized</c>.</summary>
    public static string CommandFor(string executable)
    {
        if (ScheduledScanTask.ValidateExecutable(executable, DesktopExecutableName) is { } problem) throw new ArgumentException(problem, nameof(executable));
        return $"\"{executable}\" {MinimizedArgument}";
    }

    [GeneratedRegex("\\A\"([^\"]+)\" --minimized\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    /// <summary>Executável de um valor do Run, se ele tiver exatamente o formato que o NEXUS grava; senão null.</summary>
    public static string? ParseCommand(string? value)
    {
        var m = value is null ? null : CommandPattern().Match(value);
        if (m is null || !m.Success) return null;
        var exe = m.Groups[1].Value;
        return ScheduledScanTask.ValidateExecutable(exe, DesktopExecutableName) is null ? exe : null;
    }

    private static string? TargetProblem(ActionInstance a) =>
        a.Target.GetValueOrDefault("hive") == "HKCU" && a.Target.GetValueOrDefault("path") == RunKey && a.Target.GetValueOrDefault("name") == ValueName
            ? null : $"Só o valor {ValueName} do Run da sua conta pode ser gerenciado.";

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action) is { } t) return Applicability.Blocked(t);
        if (action.Desired.Type != StateType || action.Desired.Value is not (On or Off)) return Applicability.Blocked("Valor desejado deve ser ON ou OFF.");
        if (_otherAccount) return Applicability.Blocked("Iniciar com o Windows é da sua conta: use o aplicador do usuário.");
        if (action.Desired.Value == On)
        {
            var exe = _desktopExecutable();
            if (exe is null) return Applicability.NotApplicable("Nexus.Desktop.exe não está ao lado do aplicador.");
            if (ScheduledScanTask.ValidateExecutable(exe, DesktopExecutableName) is { } bad) return Applicability.Blocked(bad);
        }
        if (Detect(action).Value == Foreign)
            return Applicability.Blocked($"Já existe uma entrada \"{ValueName}\" na inicialização que não foi criada pelo NEXUS; ela não será tocada.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action) is { } t) throw new InvalidOperationException(t);
        var read = _registry.Read(Target);
        if (!read.ValueExists) return StateSnapshot.Of(StateType, Off, ValueOrigin.Explicit, new Dictionary<string, string> { ["present"] = "0" });
        var exe = read.Kind is "String" or "ExpandString" ? ParseCommand(read.Value) : null;
        if (exe is null || read.Kind != "String") return StateSnapshot.Of(StateType, Foreign, ValueOrigin.ThirdPartyManaged);
        var detail = new Dictionary<string, string> { ["present"] = "1", ["value"] = read.Value! };
        var current = _desktopExecutable();
        var here = current is not null && string.Equals(exe, current, StringComparison.OrdinalIgnoreCase);
        return StateSnapshot.Of(StateType, here ? On : Stale, ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var now = Detect(action);
        if (now.Value == Foreign) throw new InvalidOperationException("A entrada NEXUS na inicialização não é do NEXUS; nada foi alterado.");
        if (action.Desired.Value == Off)
        {
            if (now.Value != Off) _registry.DeleteValue(Target);
            return;
        }
        if (action.Desired.Value != On) throw new InvalidOperationException("Valor desejado deve ser ON ou OFF.");
        var exe = _desktopExecutable() ?? throw new InvalidOperationException("Nexus.Desktop.exe não encontrado.");
        _registry.Write(Target, "String", CommandFor(exe));
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        var now = Detect(action);
        if (now.Value == Foreign) throw new InvalidOperationException("A entrada NEXUS na inicialização não é do NEXUS; nada foi alterado.");
        if (original.Value == Off)
        {
            if (now.Value != Off) _registry.DeleteValue(Target);
            return;
        }
        _registry.Write(Target, "String", original.Detail!["value"]);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (TargetProblem(action) is { } t) return t;
        if (!original.Exists || original.Type != StateType) return "Estado original com formato desconhecido.";
        if (original.Value == Off) return null;
        if (original.Value == Foreign) return "O estado original era de outro programa; o NEXUS não o recria.";
        if (original.Value is not (On or Stale)) return "Estado original inválido.";
        if (original.Detail is null || !original.Detail.TryGetValue("value", out var value)) return "O diário não guardou o valor anterior.";
        return ParseCommand(value) is null ? "O valor guardado não tem o formato do NEXUS." : null;
    }
}
