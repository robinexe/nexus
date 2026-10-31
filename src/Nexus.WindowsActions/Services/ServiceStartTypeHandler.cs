using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Core;

namespace Nexus.WindowsActions.Services;

/// <summary>Valores canônicos: Boot, System, Automatic, AutomaticDelayed, Manual, Disabled.</summary>
public interface IServiceBackend
{
    /// <summary>Retorna null se o serviço não existe.</summary>
    string? GetStartType(string service);
    void SetStartType(string service, string startType);
}

public sealed class ServiceStartTypeHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "ServiceStartType";
    private static readonly HashSet<string> Writable = new(StringComparer.Ordinal) { "Automatic", "AutomaticDelayed", "Manual", "Disabled" };
    private readonly IServiceBackend _svc;
    public ServiceStartTypeHandler(IServiceBackend svc) => _svc = svc;
    public string HandlerKind => Kind;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        if (!action.Target.TryGetValue("service", out var name) || string.IsNullOrWhiteSpace(name)) return Applicability.Blocked("Serviço não informado.");
        if (SafetyPolicy.CheckService(name) is { } problem) return Applicability.Blocked(problem);
        if (action.Desired.Type != "StartType" || !Writable.Contains(action.Desired.Value ?? "")) return Applicability.Blocked("Tipo de inicialização não permitido.");
        var current = _svc.GetStartType(name);
        if (current is null) return Applicability.NotApplicable($"Serviço '{name}' não existe neste sistema.");
        if (current is "Boot" or "System") return Applicability.Blocked("Drivers de boot/sistema não são alterados.");
        return Applicability.Ok;
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        if (!action.Target.TryGetValue("service", out var name) || string.IsNullOrWhiteSpace(name)) return "Serviço não informado.";
        if (SafetyPolicy.CheckService(name) is { } problem) return problem;
        if (!original.Exists) return "Original ausente: o serviço não existia; nada a restaurar.";
        if (original.Type != "StartType" || !Writable.Contains(original.Value ?? "")) return $"Tipo de inicialização original '{original.Value}' não restaurável.";
        return null;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var v = _svc.GetStartType(action.Target["service"]);
        return v is null ? StateSnapshot.Absent() : StateSnapshot.Of("StartType", v);
    }

    public void Apply(ActionInstance action) => _svc.SetStartType(action.Target["service"], action.Desired.Value!);

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        if (!original.Exists) throw new InvalidOperationException("O serviço não existia no backup; nada a restaurar.");
        _svc.SetStartType(action.Target["service"], original.Value!);
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsServiceBackend : IServiceBackend
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001, ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ConfigDelayedAutoStart = 3;
    private const int ErrorServiceDoesNotExist = 1060;

    public string? GetStartType(string service)
    {
        using var h = Open(service, ServiceQueryConfig, out var missing);
        if (missing) return null;
        QueryServiceConfigW(h.Handle, IntPtr.Zero, 0, out var needed);
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfigW(h.Handle, buf, needed, out _)) throw Win32(nameof(QueryServiceConfigW));
            var startType = (uint)Marshal.ReadInt32(buf, 4); // QUERY_SERVICE_CONFIGW.dwStartType
            if (startType == 2 && IsDelayed(h.Handle)) return "AutomaticDelayed";
            return startType switch { 0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => $"Unknown({startType})" };
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void SetStartType(string service, string startType)
    {
        using var h = Open(service, ServiceChangeConfig | ServiceQueryConfig, out var missing);
        if (missing) throw new InvalidOperationException($"Serviço '{service}' não existe.");
        uint t = startType switch { "Automatic" or "AutomaticDelayed" => 2, "Manual" => 3, "Disabled" => 4, _ => throw new ArgumentOutOfRangeException(nameof(startType)) };
        if (!ChangeServiceConfigW(h.Handle, ServiceNoChange, t, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
            throw Win32(nameof(ChangeServiceConfigW));
        if (t == 2)
        {
            var info = startType == "AutomaticDelayed" ? 1 : 0;
            if (!ChangeServiceConfig2W(h.Handle, ConfigDelayedAutoStart, ref info)) throw Win32(nameof(ChangeServiceConfig2W));
        }
    }

    private static bool IsDelayed(IntPtr svc)
    {
        var buf = Marshal.AllocHGlobal(16);
        try { return QueryServiceConfig2W(svc, ConfigDelayedAutoStart, buf, 16, out _) && Marshal.ReadInt32(buf) != 0; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static ScHandles Open(string service, uint access, out bool missing)
    {
        missing = false;
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) throw Win32(nameof(OpenSCManagerW));
        var svc = OpenServiceW(scm, service, access);
        if (svc == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            CloseServiceHandle(scm);
            if (err == ErrorServiceDoesNotExist) { missing = true; return new ScHandles(IntPtr.Zero, IntPtr.Zero); }
            throw new InvalidOperationException($"OpenServiceW falhou: {err}");
        }
        return new ScHandles(scm, svc);
    }

    private static InvalidOperationException Win32(string api) => new($"{api} falhou: {Marshal.GetLastPInvokeError()}");

    private readonly record struct ScHandles(IntPtr Manager, IntPtr Handle) : IDisposable
    {
        public void Dispose()
        {
            if (Handle != IntPtr.Zero) CloseServiceHandle(Handle);
            if (Manager != IntPtr.Zero) CloseServiceHandle(Manager);
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr h);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(IntPtr svc, IntPtr buffer, uint size, out uint needed);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig2W(IntPtr svc, uint level, IntPtr buffer, uint size, out uint needed);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(IntPtr svc, uint type, uint start, uint error, string? path, string? group,
        IntPtr tagId, string? deps, string? account, string? password, string? displayName);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(IntPtr svc, uint level, ref int info);
}
