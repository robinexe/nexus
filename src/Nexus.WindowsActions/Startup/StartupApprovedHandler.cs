using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.WindowsActions.Startup;

/// <summary>
/// Desativa/ativa item de inicialização pelo mesmo mecanismo do Gerenciador de Tarefas:
/// Explorer\StartupApproved\{Run|Run32}, binário de 12 bytes (byte 0: 0x02/0x06 habilitado, 0x03/0x07 desabilitado;
/// bytes 4..11: FILETIME). Não apaga a entrada em ...\Run. Formato observado, não documentado oficialmente.
/// Snapshot semântico: Type "StartupState", Value "enabled"/"disabled"; bytes originais em Detail["raw"].
/// </summary>
public sealed class StartupApprovedHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "StartupApproved";
    private readonly IRegistryBackend _reg;
    private readonly TimeProvider _clock;

    public StartupApprovedHandler(IRegistryBackend backend, TimeProvider? clock = null)
    {
        _reg = backend;
        _clock = clock ?? TimeProvider.System;
    }

    public string HandlerKind => Kind;

    private static RegHive HiveOf(ActionInstance a) => Enum.Parse<RegHive>(a.Target["scope"], true);

    private static RegistryTarget ApprovedTarget(ActionInstance a) =>
        new(HiveOf(a), $@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{a.Target["kind"]}", a.Target["entryName"], RegView.Default);

    private static RegistryTarget RunTarget(ActionInstance a) => a.Target["kind"] switch
    {
        "Run" => new(HiveOf(a), @"Software\Microsoft\Windows\CurrentVersion\Run", a.Target["entryName"], RegView.Registry64),
        "Run32" => new(HiveOf(a), @"Software\Microsoft\Windows\CurrentVersion\Run", a.Target["entryName"], RegView.Registry32),
        _ => throw new NotSupportedException(),
    };

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        foreach (var k in new[] { "scope", "kind", "entryName" })
            if (!action.Target.ContainsKey(k)) return Applicability.Blocked($"Alvo incompleto: falta '{k}'.");
        if (!Enum.TryParse<RegHive>(action.Target["scope"], true, out _)) return Applicability.Blocked("Escopo inválido.");
        if (action.Target["kind"] is not ("Run" or "Run32")) return Applicability.Blocked("v1 suporta somente Run e Run32.");
        if (action.Desired.Type != "StartupState" || action.Desired.Value is not ("enabled" or "disabled"))
            return Applicability.Blocked("Estado desejado deve ser enabled/disabled.");
        if (!_reg.Read(RunTarget(action)).ValueExists)
            return Applicability.NotApplicable("Entrada não encontrada em ...\\CurrentVersion\\Run.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var r = _reg.Read(ApprovedTarget(action));
        if (!r.ValueExists)
            // Ausente em StartupApproved = o Explorer trata como habilitado. Registramos a ausência explicitamente.
            return new StateSnapshot(false, null, null, ValueOrigin.Absent, new Dictionary<string, string> { ["effective"] = "enabled" });
        var raw = r.Value ?? string.Empty;
        var state = raw.Length >= 2 && (Convert.ToByte(raw[..2], 16) & 0x1) == 1 ? "disabled" : "enabled";
        return StateSnapshot.Of("StartupState", state, ValueOrigin.Explicit, new Dictionary<string, string> { ["raw"] = raw });
    }

    public void Apply(ActionInstance action)
    {
        var bytes = new byte[12];
        bytes[0] = action.Desired.Value == "disabled" ? (byte)0x03 : (byte)0x02;
        BitConverter.TryWriteBytes(bytes.AsSpan(4), _clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        _reg.Write(ApprovedTarget(action), "Binary", Convert.ToHexStringLower(bytes));
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        foreach (var k in new[] { "scope", "kind", "entryName" })
            if (!action.Target.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)) return $"Alvo incompleto: falta '{k}'.";
        if (!Enum.TryParse<RegHive>(action.Target["scope"], true, out _)) return "Escopo inválido.";
        if (action.Target["kind"] is not ("Run" or "Run32")) return "v1 suporta somente Run e Run32.";
        if (original.Exists && (original.Type != "StartupState" || original.Value is not ("enabled" or "disabled"))) return "Estado original inválido.";
        return null;
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        var t = ApprovedTarget(action);
        if (!original.Exists) { _reg.DeleteValue(t); return; }
        if (original.Detail is null || !original.Detail.TryGetValue("raw", out var raw))
            throw new InvalidOperationException("Backup sem bytes originais; restauração exata impossível.");
        _reg.Write(t, "Binary", raw);
    }
}
