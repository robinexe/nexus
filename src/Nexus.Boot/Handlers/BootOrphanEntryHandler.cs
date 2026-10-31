using Nexus.Boot.Inventory;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Boot.Handlers;

/// <summary>
/// "BootOrphanEntry": remove um valor de ...\CurrentVersion\Run cujo programa comprovadamente não existe mais (entrada órfã).
/// Backup completo (tipo + dados) no snapshot; Restore recria o valor exatamente como era.
/// Snapshot: Type "OrphanEntry", Value "present" (Detail: kind, data) ou "removed".
/// Recusa sempre que não dá para PROVAR que o arquivo não existe: variável de ambiente desconhecida (ex.: %LOCALAPPDATA% de
/// outra conta), caminho relativo, ou o arquivo existe.
/// </summary>
public sealed class BootOrphanEntryHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "BootOrphanEntry";
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private readonly IRegistryBackend _reg;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, string?> _processEnv;
    private readonly bool _otherAccount;

    /// <param name="processEnvironment">Variáveis do processo do aplicador. Só as de máquina são usadas quando ele roda como outra conta.</param>
    public BootOrphanEntryHandler(IRegistryBackend registry, Func<string, bool> fileExists, Func<string, string?> processEnvironment, bool otherAccount)
    {
        _reg = registry;
        _exists = fileExists;
        _processEnv = processEnvironment;
        _otherAccount = otherAccount;
    }

    public string HandlerKind => Kind;

    /// <summary>Variáveis iguais para qualquer conta (do computador).</summary>
    private static readonly HashSet<string> MachineVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemRoot", "windir", "SystemDrive", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "ALLUSERSPROFILE",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "PUBLIC",
    };

    private static string? TargetProblem(ActionInstance a, out RegistryTarget target)
    {
        target = null!;
        if (!a.Target.TryGetValue("scope", out var s) || !Enum.TryParse<RegHive>(s, true, out var hive)) return "Escopo inválido.";
        if (UserHive.Check(a, hive) is { } sid) return sid;
        if (!a.Target.TryGetValue("entryName", out var name) || string.IsNullOrEmpty(name) || name.Length > 260) return "Nome da entrada inválido.";
        var view = a.Target.TryGetValue("kind", out var k) ? k switch
        {
            "Run" => RegView.Registry64,
            "Run32" when hive == RegHive.HKLM => RegView.Registry32,
            _ => (RegView?)null,
        } : null;
        if (view is null) return "Tipo de entrada não suportado (Run, ou Run32 no computador).";
        target = new RegistryTarget(hive, StartupInventory.RunPath, name, view.Value, UserHive.Of(a));
        return SafetyPolicy.CheckRegistry(hive.ToString(), StartupInventory.RunPath, name);
    }

    /// <summary>Busca de variáveis para a conta dona da entrada.</summary>
    private Func<string, string?> LookupFor(RegistryTarget t)
    {
        string? Machine(string v) => MachineVariables.Contains(v) ? _processEnv(v) : null;
        if (t.Hive == RegHive.HKLM) return Machine;
        if (t.UserSid is null) return _otherAccount ? Machine : _processEnv;

        var profile = _reg.Read(new RegistryTarget(RegHive.HKLM, $@"{ProfileList}\{t.UserSid}", "ProfileImagePath", RegView.Default));
        var home = profile.ValueExists ? CommandLine.Expand(profile.Value!, Machine) : null;
        return v =>
        {
            if (home is not null)
            {
                if (v.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase)) return home;
                if (v.Equals("APPDATA", StringComparison.OrdinalIgnoreCase)) return home + @"\AppData\Roaming";
                if (v.Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase)) return home + @"\AppData\Local";
            }
            return Machine(v);
        };
    }

    /// <summary>Null se a entrada é comprovadamente órfã; senão, o motivo para não remover.</summary>
    public string? WhyNotOrphan(RegistryTarget t, RegistryRead read)
    {
        if (!read.ValueExists) return "Entrada não encontrada.";
        if (read.Kind is not ("String" or "ExpandString")) return "Entrada com tipo inesperado: não é removida.";
        var exe = CommandLine.ExecutableOf(read.Value);
        if (exe is null) return "Entrada sem programa reconhecível.";
        var expanded = CommandLine.Expand(exe, LookupFor(t));
        if (expanded is null) return "Não dá para confirmar o caminho (variável de ambiente de outra conta): nada é removido.";
        if (!CommandLine.IsAbsoluteWindowsPath(expanded)) return "Caminho relativo: não dá para provar que o programa não existe.";
        if (expanded.StartsWith(@"\\", StringComparison.Ordinal)) return "Caminho de rede: pode só estar desconectado; nada é removido.";
        if (_exists(expanded)) return "O programa existe: a entrada não é órfã (use desativar).";
        return null;
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        if (TargetProblem(action, out var t) is { } p) return Applicability.Blocked(p);
        if (action.Desired.Type != "OrphanEntry" || action.Desired.Value != "removed") return Applicability.Blocked("Estado desejado deve ser removed.");
        var read = _reg.Read(t);
        if (!read.ValueExists) return Applicability.Ok; // já removida: o plano mostra "já configurado"
        return WhyNotOrphan(t, read) is { } why ? Applicability.Blocked(why) : Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        if (TargetProblem(action, out var t) is { } p) throw new InvalidOperationException(p);
        var r = _reg.Read(t);
        return r.ValueExists
            ? StateSnapshot.Of("OrphanEntry", "present", ValueOrigin.Explicit, new Dictionary<string, string> { ["kind"] = r.Kind!, ["data"] = r.Value ?? "" })
            : StateSnapshot.Of("OrphanEntry", "removed");
    }

    public void Apply(ActionInstance action)
    {
        if (TargetProblem(action, out var t) is { } p) throw new InvalidOperationException(p);
        var read = _reg.Read(t);
        if (!read.ValueExists) return;
        if (WhyNotOrphan(t, read) is { } why) throw new InvalidOperationException(why); // revalida na hora de escrever
        _reg.DeleteValue(t);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        if (TargetProblem(action, out _) is { } p) return p;
        if (!original.Exists || original.Type != "OrphanEntry" || original.Value is not ("present" or "removed")) return "Backup inválido.";
        if (original.Value == "present" && (original.Detail is null || !original.Detail.TryGetValue("kind", out var kind) ||
                                            kind is not ("String" or "ExpandString") || !original.Detail.ContainsKey("data")))
            return "Backup sem o valor original completo; restauração exata impossível.";
        return null;
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        TargetProblem(action, out var t);
        if (original.Value == "removed") { _reg.DeleteValue(t); return; }
        _reg.Write(t, original.Detail!["kind"], original.Detail["data"]);
    }
}
