using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Measurement.Windows;

/// <summary>
/// Rastreamento do kernel (ETW, SystemTraceProvider) por P/Invoke, SÓ LEITURA, para o "Atraso de drivers" apontar o driver:
/// eventos PerfInfo DPC (tipos 66/68/69) e ISR (67) trazem InitialTime e o endereço da rotina; Image_Load (10/3/4) traz base, tamanho
/// e arquivo de cada módulo carregado (o kernel enumera os já carregados no início — DCStart). Complemento: EnumDeviceDrivers +
/// GetDeviceDriverBaseName (psapi), que dá a base de todos os drivers, sem tamanho.
/// <para>Sessão: logger de sistema privado "NEXUS-DriverLatency" (LogFileMode EVENT_TRACE_SYSTEM_LOGGER_MODE, Windows 8+; GUID
/// próprio em Wnode.Guid), para não disputar o "NT Kernel Logger" com LatencyMon/xperf; se o Windows recusar esse modo, cai para o
/// NT Kernel Logger clássico (KERNEL_LOGGER_NAME + SystemTraceControlGuid). EnableFlags = IMAGE_LOAD | INTERRUPT | DPC. Exige
/// administrador. A sessão é parada ao fim (também em erro) e nada fica no PC.</para>
/// <para>Fontes: learn.microsoft.com/windows/win32/etw/configuring-and-starting-a-systemtraceprovider-session,
/// …/configuring-and-starting-the-nt-kernel-logger-session, …/event-trace-properties, …/perfinfo, …/dpc, …/isr, …/image-load.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsKernelTrace : IKernelTraceBackend
{
    public const string SessionName = "NEXUS-DriverLatency";
    public const string KernelLoggerName = "NT Kernel Logger";
    /// <summary>GUID próprio desta sessão (um logger de sistema privado não pode usar SystemTraceControlGuid).</summary>
    private static readonly Guid SessionGuid = new("4D7C2B9E-6F3A-4E1B-9C58-2A1F0E7D3B61");
    private static readonly Guid SystemTraceControlGuid = new("9E814AAB-3204-D4D3-98E4-B50D89A4E6E1");
    private static readonly Guid PerfInfoGuid = new("CE1DBFB4-137E-4DA6-87B0-3F59AA102CBC");
    private static readonly Guid ImageGuid = new("2CB15D1D-5FC1-11D2-ABE1-00A0C911F518");

    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint EventTraceSystemLoggerMode = 0x02000000;
    private const uint EventTraceFlagImageLoad = 0x00000004;
    private const uint EventTraceFlagDpc = 0x00000020;
    private const uint EventTraceFlagInterrupt = 0x00000040;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    /// <summary>Sem este modo, ProcessTrace converte o TimeStamp do cabeçalho para hora do sistema (FILETIME) e a subtração com o
    /// InitialTime da DPC (ticks QPC crus) sai absurda — foi o que zerou a primeira rodada na VM do CI.</summary>
    private const uint ProcessTraceModeRawTimestamp = 0x00001000;
    private const uint EventTraceControlStop = 1;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorAccessDenied = 5;
    private const ulong InvalidProcessTraceHandle = ulong.MaxValue;
    private const int MaxEvents = 2_000_000;

    // EVENT_TRACE_PROPERTIES (x64): WNODE_HEADER 48 bytes + 72 bytes de campos = 120; o nome da sessão vai logo depois.
    private const int PropertiesSize = 120;
    private const int PropertiesBuffer = PropertiesSize + 1024;
    // EVENT_TRACE_LOGFILEW (x64) = 448 bytes.
    private const int LogFileSize = 448;
    private const int LogFileLoggerName = 8;
    private const int LogFileProcessTraceMode = 28;
    private const int LogFileEventRecordCallback = 424;

    private static WindowsKernelTrace? _current;

    private readonly List<KernelRoutineEvent> _events = [];
    private readonly List<KernelModule> _modules = [];
    private readonly double _usPerTick = 1e6 / Stopwatch.Frequency;
    private ulong _session;
    private string _sessionName = SessionName;
    private IntPtr _properties;

    public long Dropped { get; private set; }
    private long _callbacks;
    private readonly Dictionary<Guid, long> _byProvider = [];
    private uint _eventsLost, _buffersWritten, _realTimeLost;
    private int _openTraceError;
    private int _samples;
    private string _sample = "";

    /// <inheritdoc/>
    public string? Diagnostics
    {
        get
        {
            var providers = string.Join(", ", _byProvider.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{Name(kv.Key)}={kv.Value}"));
            return $"sessão={_sessionName}; callbacks={_callbacks}; por provedor: [{providers}]; descartados={Dropped}; " +
                   $"EventsLost={_eventsLost}; BuffersWritten={_buffersWritten}; RealTimeBuffersLost={_realTimeLost}; OpenTrace erro={_openTraceError}; " +
                   $"QPC/s={Stopwatch.Frequency}; amostras:{_sample}";
        }
    }

    private static string Name(Guid g) => g == PerfInfoGuid ? "PerfInfo" : g == ImageGuid ? "Image" : g.ToString("N")[..8];

    public (IReadOnlyList<KernelRoutineEvent> Events, IReadOnlyList<KernelModule> Modules) Capture(TimeSpan duration)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("O rastreamento do kernel do NEXUS só roda em Windows 64 bits.");
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(duration));
        if (Interlocked.CompareExchange(ref _current, this, null) is not null) throw new InvalidOperationException("Já existe um rastreamento em andamento.");
        _properties = Marshal.AllocHGlobal(PropertiesBuffer);
        var logFile = Marshal.AllocHGlobal(LogFileSize);
        var namePtr = IntPtr.Zero;
        var trace = InvalidProcessTraceHandle;
        Thread? thread = null;
        try
        {
            StartSession();
            foreach (var m in EnumerateDrivers()) _modules.Add(m);

            new Span<byte>((void*)logFile, LogFileSize).Clear();
            namePtr = Marshal.StringToHGlobalUni(_sessionName);
            var b = (byte*)logFile;
            *(IntPtr*)(b + LogFileLoggerName) = namePtr;
            *(uint*)(b + LogFileProcessTraceMode) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp;
            *(IntPtr*)(b + LogFileEventRecordCallback) = (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnEventRecord;
            trace = OpenTraceW(logFile);
            if (trace == InvalidProcessTraceHandle) { _openTraceError = Marshal.GetLastPInvokeError(); throw new InvalidOperationException($"OpenTrace falhou (código {_openTraceError})."); }
            var handle = trace;
            thread = new Thread(() => { var h = handle; _ = ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero); }) { IsBackground = true, Name = "NEXUS ETW drivers" };
            thread.Start();
            Thread.Sleep(duration);
        }
        finally
        {
            StopSession();
            if (trace != InvalidProcessTraceHandle) _ = CloseTrace(trace);
            thread?.Join(TimeSpan.FromSeconds(5));
            Marshal.FreeHGlobal(logFile);
            if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
            if (_properties != IntPtr.Zero) { Marshal.FreeHGlobal(_properties); _properties = IntPtr.Zero; }
            Interlocked.CompareExchange(ref _current, null, this);
        }
        lock (_events) return (_events.ToList(), _modules.ToList());
    }

    private void StartSession()
    {
        // 1) logger de sistema privado (nome próprio); 2) NT Kernel Logger clássico.
        var rc = TryStart(SessionName, SessionGuid, EventTraceSystemLoggerMode);
        if (rc == 0) { _sessionName = SessionName; return; }
        if (rc == ErrorAccessDenied) throw new UnauthorizedAccessException("O Windows negou a sessão de rastreamento do kernel: exige administrador.");
        var rc2 = TryStart(KernelLoggerName, SystemTraceControlGuid, 0);
        if (rc2 == 0) { _sessionName = KernelLoggerName; return; }
        if (rc2 == ErrorAccessDenied) throw new UnauthorizedAccessException("O Windows negou a sessão de rastreamento do kernel: exige administrador.");
        throw new InvalidOperationException($"StartTrace falhou (logger de sistema: código {rc}; NT Kernel Logger: código {rc2}).");
    }

    private int TryStart(string name, Guid guid, uint extraMode)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            FillProperties(_properties, guid, extraMode);
            var rc = StartTraceW(out _session, name, _properties);
            if (rc != ErrorAlreadyExists || attempt == 1) return rc;
            // Sessão esquecida com o nosso nome (processo morto): para e tenta de novo. O NT Kernel Logger de outro programa
            // (LatencyMon/xperf aberto) também é parado: é a única sessão com esse nome e só se chega aqui sem o logger privado.
            FillProperties(_properties, guid, extraMode);
            _ = ControlTraceW(0, name, _properties, EventTraceControlStop);
        }
        return ErrorAlreadyExists;
    }

    private static void FillProperties(IntPtr p, Guid guid, uint extraMode)
    {
        new Span<byte>((void*)p, PropertiesBuffer).Clear();
        var b = (byte*)p;
        *(uint*)(b + 0) = PropertiesBuffer;                       // Wnode.BufferSize
        *(Guid*)(b + 24) = guid;                                   // Wnode.Guid
        *(uint*)(b + 40) = 1;                                      // Wnode.ClientContext = QPC
        *(uint*)(b + 44) = WnodeFlagTracedGuid;                    // Wnode.Flags
        *(uint*)(b + 48) = 128;                                    // BufferSize (KB)
        *(uint*)(b + 52) = 8;                                      // MinimumBuffers
        *(uint*)(b + 56) = 64;                                     // MaximumBuffers
        *(uint*)(b + 64) = EventTraceRealTimeMode | extraMode;     // LogFileMode
        *(uint*)(b + 68) = 1;                                      // FlushTimer (s)
        *(uint*)(b + 72) = EventTraceFlagImageLoad | EventTraceFlagDpc | EventTraceFlagInterrupt; // EnableFlags
        *(uint*)(b + 116) = PropertiesSize;                        // LoggerNameOffset
    }

    private void StopSession()
    {
        if (_properties == IntPtr.Zero || _session == 0) return;
        FillProperties(_properties, Guid.Empty, 0);
        _ = ControlTraceW(_session, null, _properties, EventTraceControlStop);
        var b = (byte*)_properties;
        _eventsLost = *(uint*)(b + 88); _buffersWritten = *(uint*)(b + 92); _realTimeLost = *(uint*)(b + 100);
        _session = 0;
    }

    [UnmanagedCallersOnly]
    private static void OnEventRecord(IntPtr record)
    {
        try { _current?.Handle((byte*)record); }
        catch (Exception) { /* nunca deixar exceção atravessar a fronteira nativa */ }
    }

    /// <summary>
    /// EVENT_RECORD (x64): TimeStamp em +16, ProviderId (classe MOF) em +24, Opcode (tipo do evento) em +45, ProcessorNumber em +80,
    /// UserDataLength em +86, UserData em +96.
    /// </summary>
    private void Handle(byte* r)
    {
        var provider = *(Guid*)(r + 24);
        _callbacks++;
        lock (_byProvider) { _byProvider[provider] = _byProvider.GetValueOrDefault(provider) + 1; }
        var opcode = *(r + 45);
        var length = *(ushort*)(r + 86);
        var data = *(byte**)(r + 96);
        if (data is null) return;
        if (provider == PerfInfoGuid)
        {
            KernelRoutineKind kind;
            if (opcode is 66 or 68 or 69) kind = KernelRoutineKind.Dpc;
            else if (opcode == 67) kind = KernelRoutineKind.Isr;
            else return;
            if (length < 16) return;
            var end = *(long*)(r + 16);
            var initial = *(long*)data;
            var routine = *(ulong*)(data + 8);
            var us = (end - initial) * _usPerTick;
            if (_samples < 3) { _samples++; _sample += $" [{kind} ts={end} ini={initial} us={us:0.#}]"; }
            var ev = new KernelRoutineEvent(kind, routine, us, *(r + 80));
            lock (_events)
            {
                if (_events.Count < MaxEvents) _events.Add(ev);
                else Dropped++;
            }
        }
        else if (provider == ImageGuid && opcode is 10 or 3 or 4)
        {
            // ImageBase +0, ImageSize +8, ProcessId +16, …, DefaultBase +32, Reserved1-4 +40..+56, FileName (UTF-16, nulo) +56.
            if (length < 58) return;
            var pid = *(uint*)(data + 16);
            if (pid != 0) return; // só módulos do kernel
            var imageBase = *(ulong*)data;
            var size = *(ulong*)(data + 8);
            var name = Marshal.PtrToStringUni((IntPtr)(data + 56), (length - 56) / 2)?.TrimEnd('\0') ?? "";
            var cut = name.IndexOf('\0');
            if (cut >= 0) name = name[..cut];
            if (name.Length == 0) return;
            lock (_modules) _modules.Add(new KernelModule(imageBase, size, name));
        }
    }

    /// <summary>EnumDeviceDrivers + GetDeviceDriverBaseName (psapi): base de cada driver carregado, sem tamanho.</summary>
    private static List<KernelModule> EnumerateDrivers()
    {
        var list = new List<KernelModule>();
        if (!K32EnumDeviceDrivers(null, 0, out var needed) || needed == 0) return list;
        var bases = new IntPtr[needed / sizeof(long) + 16];
        if (!K32EnumDeviceDrivers(bases, (uint)(bases.Length * sizeof(long)), out needed)) return list;
        var count = (int)Math.Min(needed / sizeof(long), (uint)bases.Length);
        var name = stackalloc char[260];
        for (var i = 0; i < count; i++)
        {
            var n = K32GetDeviceDriverBaseNameW(bases[i], name, 260);
            if (n == 0) continue;
            list.Add(new KernelModule((ulong)bases[i].ToInt64(), 0, new string(name, 0, (int)n)));
        }
        return list;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int ProcessTrace(ulong* handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int CloseTrace(ulong traceHandle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EnumDeviceDrivers([Out] IntPtr[]? imageBases, uint cb, out uint needed);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint K32GetDeviceDriverBaseNameW(IntPtr imageBase, char* baseName, uint size);
}
