using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Nexus.Lab.Memory.Problems;

/// <summary>Chamadas nativas compartilhadas pelos leitores e backends do diagnóstico.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DoctorNative
{
    public const uint ProcessQueryLimited = 0x1000, ProcessSetQuota = 0x0100;
    public const uint ScManagerConnect = 0x0001, ScManagerEnumerate = 0x0004;
    public const uint ServiceQueryStatus = 0x0004, ServiceEnumerateDependents = 0x0008, ServiceStart = 0x0010, ServiceStop = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EnumServiceStatusProcess
    {
        public nint ServiceName, DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(int pid, out int session);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyWorkingSet(nint process);

    [LibraryImport("user32.dll")]
    public static partial uint GetGuiResources(nint process, uint flags);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int NtSetSystemInformation(int infoClass, void* buffer, uint length);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenService(nint scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumServicesStatusEx(nint scm, int infoLevel, uint serviceType, uint serviceState, byte* buffer, uint size,
        out uint needed, out uint returned, ref uint resume, string? group);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatusEx(nint service, int infoLevel, byte* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumDependentServicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDependentServices(nint service, uint state, byte* buffer, uint size, out uint needed, out uint returned);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ControlService(nint service, uint control, out ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StartService(nint service, uint argc, nint argv);

    /// <summary>Horário de criação (FILETIME UTC bruto), caminho e sessão. Null se não der para abrir o processo.</summary>
    public static ProcessIdentity? Identify(int pid, string fallbackName)
    {
        var h = OpenProcess(ProcessQueryLimited, false, pid);
        if (h == 0) return null;
        try
        {
            if (!GetProcessTimes(h, out var created, out _, out _, out _)) return null;
            var path = ImagePath(h);
            var name = path is null ? fallbackName : Path.GetFileNameWithoutExtension(path);
            var session = ProcessIdToSessionId(pid, out var s) ? s : -1;
            return new ProcessIdentity(pid, name, created, session, path);
        }
        finally { CloseHandle(h); }
    }

    public static string? ImagePath(nint h)
    {
        var buf = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
    }

    /// <summary>Serviços ativos (Win32) com o PID de cada um — uma única chamada ao SCM.</summary>
    public static List<(string Name, string Display, int Pid, uint ServiceType)> ActiveServices()
    {
        var list = new List<(string, string, int, uint)>();
        var scm = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerate);
        if (scm == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenSCManager falhou.");
        try
        {
            uint resume = 0;
            var size = 64 * 1024u;
            while (true)
            {
                var buffer = new byte[size];
                bool ok;
                uint needed, returned;
                fixed (byte* p = buffer)
                {
                    ok = EnumServicesStatusEx(scm, 0, 0x30, 1, p, size, out needed, out returned, ref resume, null);
                    var err = ok ? 0 : Marshal.GetLastPInvokeError();
                    if (!ok && err != 234) throw new Win32Exception(err, "EnumServicesStatusEx falhou.");
                    var itemSize = sizeof(EnumServiceStatusProcess);
                    for (var i = 0; i < returned; i++)
                    {
                        var e = *(EnumServiceStatusProcess*)(p + i * itemSize);
                        var name = Marshal.PtrToStringUni(e.ServiceName) ?? "";
                        var display = Marshal.PtrToStringUni(e.DisplayName) ?? name;
                        list.Add((name, display, (int)e.Status.ProcessId, e.Status.ServiceType));
                    }
                }
                if (ok) break;
                size = Math.Max(size, needed + 4096);
            }
        }
        finally { CloseServiceHandle(scm); }
        return list;
    }
}

// ============================================================================ leitura (somente leitura)

/// <summary>Leitura completa para o diagnóstico (nenhuma escreve). Leva ~0,3–1 s; rode em Task.Run.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class WindowsDoctorReader
{
    private const int SystemPoolTagInformation = 22;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

    [GeneratedRegex(@"pid_(\d+)_", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GpuPidRx();

    public static MemorySnapshot Read(bool includePoolTags = true)
    {
        var problems = new Dictionary<string, string>();
        var system = ReadSystem(problems);

        var procs = new List<ProcSample>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var sample = new ProcSample(p.Id, p.ProcessName, p.WorkingSet64, p.PrivateMemorySize64, p.HandleCount, p.Threads.Count, p.SessionId);
                    var h = DoctorNative.OpenProcess(DoctorNative.ProcessQueryLimited, false, p.Id);
                    if (h != 0)
                    {
                        try
                        {
                            long? created = DoctorNative.GetProcessTimes(h, out var c, out _, out _, out _) ? c : null;
                            var gdi = DoctorNative.GetGuiResources(h, 0);
                            var user = DoctorNative.GetGuiResources(h, 1);
                            sample = sample with
                            {
                                StartTimeUtcTicks = created,
                                Path = DoctorNative.ImagePath(h),
                                GdiObjects = p.SessionId == 0 ? null : (int)gdi,
                                UserObjects = p.SessionId == 0 ? null : (int)user,
                            };
                        }
                        finally { DoctorNative.CloseHandle(h); }
                    }
                    procs.Add(sample);
                }
                catch (InvalidOperationException) { } // saiu durante a leitura
            }
        }

        var services = new Dictionary<int, IReadOnlyList<ServiceInfo>>();
        try
        {
            foreach (var g in DoctorNative.ActiveServices().Where(s => s.Pid > 0).GroupBy(s => s.Pid))
                services[g.Key] = g.Select(s => new ServiceInfo(s.Name, s.Display)).ToList();
        }
        catch (Win32Exception ex) { problems["serviços"] = ex.Message; }

        IReadOnlyList<PoolTagUsage>? tags = null;
        if (includePoolTags)
        {
            tags = ReadPoolTags(out var why);
            if (why is not null) problems["tags do pool"] = why;
        }

        var gpuByPid = new Dictionary<int, long>();
        try
        {
            var shared = WindowsMemoryReader.Pdh.SampleInstances(@"\GPU Adapter Memory(*)\Shared Usage");
            var dedicated = WindowsMemoryReader.Pdh.SampleInstances(@"\GPU Adapter Memory(*)\Dedicated Usage");
            system = system with
            {
                GpuShared = shared.Count == 0 ? null : (long)shared.Values.Sum(),
                GpuDedicated = dedicated.Count == 0 ? null : (long)dedicated.Values.Sum(),
            };
            foreach (var (instance, value) in WindowsMemoryReader.Pdh.SampleInstances(@"\GPU Process Memory(*)\Shared Usage"))
            {
                var m = GpuPidRx().Match(instance);
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                    gpuByPid[pid] = gpuByPid.GetValueOrDefault(pid) + (long)value;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { problems["GPU"] = ex.Message; }

        return new MemorySnapshot
        {
            At = DateTimeOffset.Now, System = system, Processes = procs, ServicesByPid = services, PoolTags = tags, GpuSharedByPid = gpuByPid,
            Problems = problems,
        };
    }

    private static SystemMemorySample ReadSystem(Dictionary<string, string> problems)
    {
        var s = new SystemMemorySample();
        var ms = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        if (GlobalMemoryStatusEx(ref ms)) s = s with { Total = (long)ms.TotalPhys, Available = (long)ms.AvailPhys };
        else problems["memória"] = "GlobalMemoryStatusEx falhou.";
        var pi = new PerformanceInformation { Cb = (uint)sizeof(PerformanceInformation) };
        if (GetPerformanceInfo(ref pi, pi.Cb))
        {
            var page = (long)pi.PageSize;
            s = s with
            {
                CommitTotal = (long)pi.CommitTotal * page, CommitLimit = (long)pi.CommitLimit * page, CommitPeak = (long)pi.CommitPeak * page,
                PagedPool = (long)pi.KernelPaged * page, NonPagedPool = (long)pi.KernelNonpaged * page, SystemCache = (long)pi.SystemCache * page,
            };
        }
        else problems["commit"] = "GetPerformanceInfo falhou.";
        try
        {
            var c = WindowsMemoryReader.Pdh.Sample(
                [@"\Memory\Standby Cache Core Bytes", @"\Memory\Standby Cache Normal Priority Bytes", @"\Memory\Standby Cache Reserve Bytes",
                 @"\Memory\Modified Page List Bytes", @"\Memory\Free & Zero Page List Bytes"], TimeSpan.Zero, CancellationToken.None);
            long? L(int i) => c[i] is { } v && v >= 0 ? (long)v : null;
            long? standby = L(0) is null && L(1) is null && L(2) is null ? null : (L(0) ?? 0) + (L(1) ?? 0) + (L(2) ?? 0);
            s = s with { Standby = standby, Modified = L(3), FreeZero = L(4) };
        }
        catch (InvalidOperationException ex) { problems["contadores"] = ex.Message; }
        return s;
    }

    /// <summary>Tags do pool (classe 22, a mesma do PoolMon). Sem permissão, devolve null e o motivo.</summary>
    public static IReadOnlyList<PoolTagUsage>? ReadPoolTags(out string? why)
    {
        why = null;
        var size = 256 * 1024u;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = new byte[size];
            int status;
            uint returned;
            fixed (byte* p = buffer) status = DoctorNative.NtQuerySystemInformation(SystemPoolTagInformation, p, size, out returned);
            if (status == StatusInfoLengthMismatch) { size = Math.Max(size * 2, returned + 16 * 1024); continue; }
            if (status < 0)
            {
                why = status == unchecked((int)0xC0000022)
                    ? "O Windows só mostra as tags do pool com administrador. O total do pool continua visível."
                    : string.Create(CultureInfo.InvariantCulture, $"NtQuerySystemInformation devolveu 0x{status:X8}.");
                return null;
            }
            return PoolTagParser.Parse(buffer.AsSpan(0, (int)Math.Min(returned == 0 ? size : returned, size)), Environment.Is64BitProcess);
        }
        why = "A lista de tags do pool mudou de tamanho durante a leitura.";
        return null;
    }

    /// <summary>pooltag.txt das Ferramentas de Depuração, se estiver instalado (não baixa nada).</summary>
    public static IReadOnlyDictionary<string, (string Owner, string Description)>? ReadLocalPoolTagTxt()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var arch in new[] { "x64", "arm64", "x86" })
        {
            var path = Path.Combine(pf86, "Windows Kits", "10", "Debuggers", arch, "triage", "pooltag.txt");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length < 8 * 1024 * 1024) return KnownPoolTags.ParsePoolTagTxt(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// Procura a tag (texto ASCII) dentro dos drivers de <c>%SystemRoot%\System32\drivers\*.sys</c>, como o
    /// <c>findstr /m /l /s</c> recomendado pela Microsoft. Só leitura; pula links e arquivos acima de 64 MB.
    /// </summary>
    public static IReadOnlyList<string> FindDriversWithTag(string tag, int max = 6, CancellationToken cancel = default)
    {
        var found = new List<string>();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
        if (!Directory.Exists(dir)) return found;
        foreach (var file in Directory.EnumerateFiles(dir, "*.sys", SearchOption.TopDirectoryOnly))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(file);
                if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint) || fi.Length > 64 * 1024 * 1024) continue;
                if (KnownPoolTags.BinaryContainsTag(File.ReadAllBytes(file), tag)) found.Add(fi.Name);
                if (found.Count >= max) break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>Algum processo aberto parece um jogo (pasta de loja/launcher)? Devolve o nome do primeiro.</summary>
    public static string? RunningGame(IReadOnlyList<ProcSample> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return processes.FirstOrDefault(p => p.SessionId != 0 && ProcessProtection.LooksLikeGame(p.Path))?.Name;
    }
}

// ============================================================================ backends de escrita (usados SÓ pelo aplicador)

[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsStandbyList : IStandbyListBackend
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;

    public void PurgeStandby()
    {
        if (!WindowsLargePages.EnablePrivilege("SeProfileSingleProcessPrivilege"))
            throw new InvalidOperationException("O aplicador não tem o direito de gerenciar a memória do sistema (precisa de administrador).");
        var command = MemoryPurgeStandbyList;
        var status = DoctorNative.NtSetSystemInformation(SystemMemoryListInformation, &command, sizeof(int));
        if (status < 0) throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"O Windows recusou esvaziar a lista de espera (0x{status:X8})."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessControl : IProcessControlBackend
{
    public ProcessIdentity? Find(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return DoctorNative.Identify(pid, p.ProcessName);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public void Trim(int pid)
    {
        var h = DoctorNative.OpenProcess(DoctorNative.ProcessQueryLimited | DoctorNative.ProcessSetQuota, false, pid);
        if (h == 0) throw new InvalidOperationException("Sem permissão para este processo (de outra conta ou elevado).");
        try
        {
            if (!DoctorNative.EmptyWorkingSet(h)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "EmptyWorkingSet falhou.");
        }
        finally { DoctorNative.CloseHandle(h); }
    }

    public bool CloseGracefully(int pid, TimeSpan wait)
    {
        using var p = Process.GetProcessById(pid);
        if (!p.CloseMainWindow()) return false;
        return p.WaitForExit(wait);
    }

    public void Terminate(int pid)
    {
        using var p = Process.GetProcessById(pid);
        p.Kill(entireProcessTree: false);
        p.WaitForExit(TimeSpan.FromSeconds(10));
    }
}

[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsServiceControl : IServiceControlBackend
{
    private const uint StateStopped = 1, StateRunning = 4, AcceptStop = 1, ControlStop = 1;

    private static string StateName(uint s) => s switch
    {
        1 => "Stopped", 2 => "StartPending", 3 => "StopPending", 4 => "Running", 5 => "ContinuePending", 6 => "PausePending", 7 => "Paused",
        _ => "Unknown",
    };

    private static nint Open(string name, uint access, out nint scm)
    {
        scm = DoctorNative.OpenSCManager(null, null, DoctorNative.ScManagerConnect);
        if (scm == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenSCManager falhou.");
        var svc = DoctorNative.OpenService(scm, name, access);
        if (svc == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            DoctorNative.CloseServiceHandle(scm);
            scm = 0;
            if (err == 1060) return 0;
            throw new Win32Exception(err, "OpenService falhou.");
        }
        return svc;
    }

    private static DoctorNative.ServiceStatusProcess Status(nint svc)
    {
        DoctorNative.ServiceStatusProcess s;
        if (!DoctorNative.QueryServiceStatusEx(svc, 0, (byte*)&s, (uint)sizeof(DoctorNative.ServiceStatusProcess), out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryServiceStatusEx falhou.");
        return s;
    }

    public ServiceRunInfo? Query(string service)
    {
        var svc = Open(service, DoctorNative.ServiceQueryStatus | DoctorNative.ServiceEnumerateDependents, out var scm);
        if (svc == 0) return null;
        try
        {
            var s = Status(svc);
            DoctorNative.EnumDependentServices(svc, 1, null, 0, out var needed, out var count);
            if (needed > 0)
            {
                var buf = new byte[needed];
                fixed (byte* p = buf) if (!DoctorNative.EnumDependentServices(svc, 1, p, needed, out _, out count)) count = 0;
            }
            var shares = s.ProcessId != 0 && DoctorNative.ActiveServices().Count(x => x.Pid == (int)s.ProcessId) > 1;
            return new ServiceRunInfo(service, StateName(s.CurrentState), (int)s.ProcessId, shares, (int)count, (s.ControlsAccepted & AcceptStop) != 0);
        }
        finally { DoctorNative.CloseServiceHandle(svc); DoctorNative.CloseServiceHandle(scm); }
    }

    public void StopService(string service, TimeSpan timeout)
    {
        var svc = Open(service, DoctorNative.ServiceStop | DoctorNative.ServiceQueryStatus, out var scm);
        if (svc == 0) throw new InvalidOperationException("Serviço não existe.");
        try
        {
            if (!DoctorNative.ControlService(svc, ControlStop, out _))
                throw new InvalidOperationException($"O serviço recusou parar (erro {Marshal.GetLastPInvokeError()}).");
            Wait(svc, StateStopped, timeout, "parar");
        }
        finally { DoctorNative.CloseServiceHandle(svc); DoctorNative.CloseServiceHandle(scm); }
    }

    public void StartService(string service, TimeSpan timeout)
    {
        var svc = Open(service, DoctorNative.ServiceStart | DoctorNative.ServiceQueryStatus, out var scm);
        if (svc == 0) throw new InvalidOperationException("Serviço não existe.");
        try
        {
            if (!DoctorNative.StartService(svc, 0, 0))
            {
                var err = Marshal.GetLastPInvokeError();
                if (err != 1056) throw new InvalidOperationException($"O serviço não iniciou (erro {err}).");
            }
            Wait(svc, StateRunning, timeout, "iniciar");
        }
        finally { DoctorNative.CloseServiceHandle(svc); DoctorNative.CloseServiceHandle(scm); }
    }

    private static void Wait(nint svc, uint state, TimeSpan timeout, string verb)
    {
        var sw = Stopwatch.StartNew();
        while (Status(svc).CurrentState != state)
        {
            if (sw.Elapsed > timeout) throw new TimeoutException($"O serviço não terminou de {verb} em {timeout.TotalSeconds:0} s.");
            Thread.Sleep(250);
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsMemoryAgent : IMemoryAgentBackend
{
    public bool? GetCompression()
    {
        var output = WindowsMemoryReader.RunPowerShell("(Get-MMAgent).MemoryCompression", TimeSpan.FromSeconds(30), CancellationToken.None)?.Trim();
        return output switch { "True" => true, "False" => false, _ => null };
    }

    public void SetCompression(bool enabled)
    {
        var verb = enabled ? "Enable-MMAgent" : "Disable-MMAgent";
        var output = WindowsMemoryReader.RunPowerShell($"$ErrorActionPreference='Stop'; {verb} -MemoryCompression; 'ok'", TimeSpan.FromSeconds(60), CancellationToken.None);
        if (output is null || !output.Contains("ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"{verb} falhou (o serviço SysMain precisa estar ativo para mudar a compressão).");
    }
}
