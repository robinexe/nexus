using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.SystemTools.Crash;

/// <summary>
/// "SystemCrashLocalDumps": pede ao Relatório de Erros do Windows para guardar um minidespejo (DumpType=1) dos próximos
/// travamentos de UM jogo, no máximo 3 (DumpCount=3), na pasta padrão (%LOCALAPPDATA%\CrashDumps; DumpFolder não é tocado).
/// Chave: HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\&lt;exe&gt; (documentada pela Microsoft:
/// https://learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps). É diagnóstico: não muda desempenho nem segurança.
/// Alvo do catálogo: {"exe": "{gameExe}"} (nome do executável, sem pasta, validado por regex no broker e de novo aqui).
/// Estado: "on" quando DumpType=1 e DumpCount=3; senão "off", com os valores anteriores no detalhe para o Restore regravar (ou
/// remover o que o NEXUS criou, inclusive a chave do jogo e a LocalDumps, se ficaram vazias e não existiam antes).
/// </summary>
public sealed partial class LocalDumpsHandler(IRegistryBackend registry) : IActionHandler, IRestoreValidator
{
    public const string Kind = "SystemCrashLocalDumps";
    public const string StateType = "WerLocalDumps";
    public const string On = "on";
    public const string Off = "off";
    public const string ParentPath = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";
    public const string DumpTypeValue = "1";
    public const string DumpCountValue = "3";
    private const string KeyExisted = "keyExisted", ParentExisted = "parentExisted", PrevType = "dumpType", PrevCount = "dumpCount";

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9 ._()+\-]{0,100}\.exe\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExePattern();

    public string HandlerKind => Kind;

    public static StateSnapshot DesiredState => StateSnapshot.Of(StateType, On);

    public static bool IsValidExe(string? exe) => exe is not null && ExePattern().IsMatch(exe) && !exe.Contains("..", StringComparison.Ordinal);

    private static string? ExeOf(ActionInstance a, out string? error)
    {
        error = null;
        if (a.Target.Count != 1 || !a.Target.TryGetValue("exe", out var exe) || !IsValidExe(exe)) { error = "Alvo inválido: só o nome de um executável (ex.: jogo.exe)."; return null; }
        var path = KeyPath(exe!);
        if (SafetyPolicy.CheckRegistry("HKLM", path, "DumpType") is { } blocked) { error = blocked; return null; }
        return exe;
    }

    public static string KeyPath(string exe) => ParentPath + "\\" + exe;

    private static RegistryTarget T(string exe, string name) => new(RegHive.HKLM, KeyPath(exe), name, RegView.Default);

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (ExeOf(action, out var error) is null) return Applicability.Blocked(error!);
        if (!action.Desired.SameValueAs(DesiredState)) return Applicability.Blocked("Valor desejado inválido (só \"on\").");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var exe = ExeOf(action, out var error) ?? throw new InvalidOperationException(error);
        var type = registry.Read(T(exe, "DumpType"));
        var count = registry.Read(T(exe, "DumpCount"));
        var parent = registry.Read(new RegistryTarget(RegHive.HKLM, ParentPath, "DumpType", RegView.Default));
        var detail = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyExisted] = type.KeyExists ? "true" : "false",
            [ParentExisted] = parent.KeyExists ? "true" : "false",
            [PrevType] = Encode(type),
            [PrevCount] = Encode(count),
        };
        var on = Is(type, DumpTypeValue) && Is(count, DumpCountValue);
        return StateSnapshot.Of(StateType, on ? On : Off, ValueOrigin.Explicit, detail);
    }

    private static bool Is(RegistryRead r, string value) => r.ValueExists && r.Kind == "DWord" && r.Value == value;

    private static string Encode(RegistryRead r) => r.ValueExists && r.Kind is not null && r.Value is not null ? r.Kind + ":" + r.Value : "";

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var exe = ExeOf(action, out var error) ?? throw new InvalidOperationException(error);
        registry.Write(T(exe, "DumpType"), "DWord", DumpTypeValue);
        registry.Write(T(exe, "DumpCount"), "DWord", DumpCountValue);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        var exe = ExeOf(action, out _)!;
        var d = original.Detail!;
        Put(T(exe, "DumpType"), d[PrevType]);
        Put(T(exe, "DumpCount"), d[PrevCount]);
        if (d[KeyExisted] == "false") registry.DeleteKeyIfEmpty(RegHive.HKLM, KeyPath(exe), RegView.Default);
        if (d[ParentExisted] == "false") registry.DeleteKeyIfEmpty(RegHive.HKLM, ParentPath, RegView.Default);
    }

    private void Put(RegistryTarget t, string encoded)
    {
        if (encoded.Length == 0) { registry.DeleteValue(t); return; }
        var colon = encoded.IndexOf(':', StringComparison.Ordinal);
        registry.Write(t, encoded[..colon], encoded[(colon + 1)..]);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (ExeOf(action, out var error) is null) return error;
        if (original.Type != StateType || original.Value is not (On or Off)) return "Estado original inválido para LocalDumps.";
        if (original.Detail is not { } d || !d.TryGetValue(KeyExisted, out var k) || k is not ("true" or "false") ||
            !d.TryGetValue(ParentExisted, out var p) || p is not ("true" or "false") ||
            !d.TryGetValue(PrevType, out var t) || !d.TryGetValue(PrevCount, out var c))
            return "Estado original sem os valores anteriores: restauração recusada.";
        foreach (var v in new[] { t, c })
        {
            if (v.Length == 0) continue;
            var colon = v.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || v[..colon] is not ("DWord" or "QWord") ||
                !ulong.TryParse(v[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return $"Valor anterior '{v}' não restaurável.";
        }
        return null;
    }
}
