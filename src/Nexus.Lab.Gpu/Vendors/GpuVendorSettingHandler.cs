using System.Diagnostics;
using Nexus.Core;

namespace Nexus.Lab.Gpu.Vendors;

/// <summary>
/// Handler "GpuVendorSetting": ajustes globais do driver AMD (ADLX) e Intel (IGCL) da lista fechada <see cref="GpuVendorTool.Settings"/>.
/// O código do fabricante roda num processo separado (<see cref="GpuVendorTool"/>): falha ou travamento ali vira "não se aplica"
/// ou erro, nunca derruba o aplicador. Alvo: <c>setting</c> (ex.: <c>amd/antilag</c>). Desejado: Type "VendorValue".
/// Desfazer devolve o valor lido antes.
/// </summary>
public sealed class GpuVendorSettingHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "GpuVendorSetting";
    public const string StateType = "VendorValue";

    /// <summary>Executa o processo separado: (args) → (código de saída, linha de saída). Nos testes, um falso.</summary>
    private readonly Func<string[], (int Code, string Line)> _run;

    public GpuVendorSettingHandler(Func<string[], (int Code, string Line)> run) => _run = run ?? throw new ArgumentNullException(nameof(run));
    public string HandlerKind => Kind;

    private static string? Setting(ActionInstance a) =>
        a.Target.Count == 1 && a.Target.TryGetValue("setting", out var s) && GpuVendorTool.Settings.ContainsKey(s) ? s : null;

    private (bool Ok, bool Unsupported, string Text) Call(params string[] args)
    {
        var (code, line) = _run(args);
        if (line.StartsWith("ok:", StringComparison.Ordinal)) return (true, false, line[3..]);
        if (line.StartsWith("unsupported:", StringComparison.Ordinal) || code == 3) return (false, true, line.Contains(':', StringComparison.Ordinal) ? line[(line.IndexOf(':') + 1)..] : line);
        return (false, false, line.StartsWith("error:", StringComparison.Ordinal) ? line[6..] : $"código {code}: {line}");
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (Setting(action) is not { } s) return Applicability.Blocked("Ajuste de driver fora da lista permitida.");
        if (action.Desired.Type != StateType || !GpuVendorTool.IsValid(s, action.Desired.Value) || action.Desired.Value is null)
            return Applicability.Blocked("Valor fora da lista permitida.");
        var (ok, unsupported, text) = Call("get", s);
        if (unsupported) return Applicability.NotApplicable(text);
        return ok ? Applicability.Ok : Applicability.Blocked("Não deu para ler o ajuste do driver: " + text);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (Setting(action) is not { } s) return "Ajuste inválido no diário.";
        if (original.Exists && (original.Type != StateType || !GpuVendorTool.IsValid(s, original.Value))) return "Valor original inválido no diário.";
        return null;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var s = Setting(action) ?? throw new InvalidOperationException("Ajuste inválido.");
        var (ok, unsupported, text) = Call("get", s);
        if (ok) return StateSnapshot.Of(StateType, text, ValueOrigin.Explicit);
        if (unsupported) return StateSnapshot.Absent();
        throw new InvalidOperationException(text);
    }

    public void Apply(ActionInstance action)
    {
        var s = Setting(action) ?? throw new InvalidOperationException("Ajuste inválido.");
        if (!GpuVendorTool.IsValid(s, action.Desired.Value) || action.Desired.Value is null) throw new InvalidOperationException("Valor fora da lista.");
        var (ok, _, text) = Call("set", s, action.Desired.Value);
        if (!ok) throw new InvalidOperationException(text);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var s = Setting(action) ?? throw new InvalidOperationException("Ajuste inválido.");
        if (!original.Exists) return; // não havia o ajuste: nada a devolver
        var (ok, _, text) = Call("set", s, original.Value!);
        if (!ok) throw new InvalidOperationException(text);
    }

    /// <summary>
    /// O processo separado de verdade: nexus-broker.exe ao lado deste programa, com <see cref="GpuVendorTool.Switch"/>, sem
    /// janela, com limite de 20 s (travou = encerrado e tratado como erro).
    /// </summary>
    public static (int Code, string Line) RunOutOfProcess(string[] args)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "nexus-broker.exe" : "nexus-broker");
        if (!File.Exists(exe)) return (3, "unsupported:nexus-broker.exe não encontrado ao lado do NEXUS");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add(GpuVendorTool.Switch);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (1, "error:não deu para iniciar o processo do driver");
            var output = p.StandardOutput.ReadLineAsync();
            if (!p.WaitForExit(20_000))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (1, "error:o driver não respondeu em 20 s");
            }
            var line = output.Wait(2_000) ? output.Result ?? "" : "";
            return (p.ExitCode, line);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (1, "error:" + ex.Message);
        }
    }
}
