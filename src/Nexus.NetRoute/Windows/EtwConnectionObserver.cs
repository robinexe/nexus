using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.NetRoute.Discovery;

namespace Nexus.NetRoute.Windows;

/// <summary>
/// Descoberta do servidor da partida por ETW (provedor Microsoft-Windows-Kernel-Network), SÓ LEITURA: o Windows conta cada
/// pacote TCP/UDP enviado e recebido com o processo dono, sem tocar no tráfego do jogo. Precisa de administrador ou de estar no
/// grupo "Usuários do log de desempenho" (a mesma permissão que o Medir já pede para o PresentMon).
/// <para>Formato dos eventos (manifesto do provedor): UserData começa com PID (u32), tamanho (u32), daddr, saddr, dport, sport
/// (portas em ordem de rede). IDs: 10/11 TCP IPv4 envio/recebimento, 26/27 TCP IPv6, 42/43 UDP IPv4, 58/59 UDP IPv6. O
/// endereço REMOTO é escolhido comparando com os endereços locais do PC, então a ordem daddr/saddr não importa.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class EtwConnectionObserver : IConnectionObserver
{
    public const string SessionName = "NEXUS-NetRoute";
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint EventTraceControlStop = 1;
    private const uint EventControlCodeEnableProvider = 1;
    private const byte TraceLevelVerbose = 5;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorAccessDenied = 5;
    private const ulong InvalidProcessTraceHandle = ulong.MaxValue;

    // EVENT_TRACE_PROPERTIES (x64): WNODE_HEADER 48 bytes + 72 bytes de campos = 120; o nome da sessão vai logo depois.
    private const int PropertiesSize = 120;
    private const int PropertiesBuffer = PropertiesSize + 1024;
    // EVENT_TRACE_LOGFILEW (x64) = 448 bytes; deslocamentos usados abaixo.
    private const int LogFileSize = 448;
    private const int LogFileLoggerName = 8;
    private const int LogFileProcessTraceMode = 28;
    private const int LogFileEventRecordCallback = 424;

    private static EtwConnectionObserver? _current;

    private ulong _session;
    private ulong _trace = InvalidProcessTraceHandle;
    private Thread? _thread;
    private Action<FlowEvent>? _onEvent;
    private HashSet<IPAddress> _local = [];
    private IntPtr _properties;
    private IntPtr _logFile;
    private IntPtr _namePtr;

    public long Parsed { get; private set; }
    public long Ignored { get; private set; }

    public void Start(Action<FlowEvent> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("A descoberta por ETW do NEXUS só roda em Windows 64 bits.");
        if (Interlocked.CompareExchange(ref _current, this, null) is not null) throw new InvalidOperationException("Já existe uma descoberta em andamento.");
        _onEvent = onEvent;
        _local = LocalAddresses();
        try
        {
            StartSession();
            OpenAndProcess();
        }
        catch
        {
            StopObserving();
            throw;
        }
    }

    private void StartSession()
    {
        _properties = Marshal.AllocHGlobal(PropertiesBuffer);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            FillProperties(_properties);
            var rc = StartTraceW(out _session, SessionName, _properties);
            if (rc == 0) break;
            if (rc == ErrorAlreadyExists && attempt == 0)
            {
                // Sessão esquecida de uma execução anterior (processo morto): para e tenta de novo.
                FillProperties(_properties);
                _ = ControlTraceW(0, SessionName, _properties, EventTraceControlStop);
                continue;
            }
            if (rc == ErrorAccessDenied)
                throw new UnauthorizedAccessException("O Windows negou a sessão de rastreamento de rede. Use 'Liberar medição' (grupo Usuários do log de desempenho) ou rode como administrador.");
            throw new InvalidOperationException($"StartTrace falhou (código {rc}).");
        }
        var provider = KernelNetwork;
        var en = EnableTraceEx2(_session, ref provider, EventControlCodeEnableProvider, TraceLevelVerbose, ulong.MaxValue, 0, 0, IntPtr.Zero);
        if (en != 0)
        {
            if (en == ErrorAccessDenied) throw new UnauthorizedAccessException("O Windows negou ligar o provedor de rede do kernel (permissão).");
            throw new InvalidOperationException($"EnableTraceEx2 falhou (código {en}).");
        }
    }

    private static void FillProperties(IntPtr p)
    {
        new Span<byte>((void*)p, PropertiesBuffer).Clear();
        var b = (byte*)p;
        *(uint*)(b + 0) = PropertiesBuffer;            // Wnode.BufferSize
        *(uint*)(b + 40) = 1;                           // Wnode.ClientContext = QPC
        *(uint*)(b + 44) = WnodeFlagTracedGuid;         // Wnode.Flags
        *(uint*)(b + 48) = 64;                          // BufferSize (KB)
        *(uint*)(b + 52) = 4;                           // MinimumBuffers
        *(uint*)(b + 56) = 32;                          // MaximumBuffers
        *(uint*)(b + 64) = EventTraceRealTimeMode;      // LogFileMode
        *(uint*)(b + 68) = 1;                           // FlushTimer (s)
        *(uint*)(b + 116) = PropertiesSize;             // LoggerNameOffset
    }

    private void OpenAndProcess()
    {
        _logFile = Marshal.AllocHGlobal(LogFileSize);
        new Span<byte>((void*)_logFile, LogFileSize).Clear();
        _namePtr = Marshal.StringToHGlobalUni(SessionName);
        var b = (byte*)_logFile;
        *(IntPtr*)(b + LogFileLoggerName) = _namePtr;
        *(uint*)(b + LogFileProcessTraceMode) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord;
        *(IntPtr*)(b + LogFileEventRecordCallback) = (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnEventRecord;
        _trace = OpenTraceW(_logFile);
        if (_trace == InvalidProcessTraceHandle) throw new InvalidOperationException($"OpenTrace falhou (código {Marshal.GetLastPInvokeError()}).");
        var handle = _trace;
        _thread = new Thread(() =>
        {
            var h = handle;
            _ = ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero);
        })
        { IsBackground = true, Name = "NEXUS ETW rede" };
        _thread.Start();
    }

    [UnmanagedCallersOnly]
    private static void OnEventRecord(IntPtr record)
    {
        try { _current?.Handle((byte*)record); }
        catch (Exception) { /* nunca deixar exceção atravessar a fronteira nativa */ }
    }

    /// <summary>EVENT_RECORD (x64): ProviderId em +24, Id do evento em +40, UserDataLength em +86, UserData em +96.</summary>
    private void Handle(byte* r)
    {
        var provider = *(Guid*)(r + 24);
        if (provider != KernelNetwork) return;
        var id = *(ushort*)(r + 40);
        var length = *(ushort*)(r + 86);
        var data = *(byte**)(r + 96);
        if (data is null) return;
        var parsed = EtwKernelNetwork.Parse(id, new ReadOnlySpan<byte>(data, length), _local, DateTimeOffset.UtcNow);
        if (parsed is null) { Ignored++; return; }
        Parsed++;
        _onEvent?.Invoke(parsed);
    }

    public void StopObserving()
    {
        if (_properties != IntPtr.Zero)
        {
            FillProperties(_properties);
            _ = ControlTraceW(_session, null, _properties, EventTraceControlStop);
        }
        if (_trace != InvalidProcessTraceHandle) { _ = CloseTrace(_trace); _trace = InvalidProcessTraceHandle; }
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        if (_logFile != IntPtr.Zero) { Marshal.FreeHGlobal(_logFile); _logFile = IntPtr.Zero; }
        if (_namePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_namePtr); _namePtr = IntPtr.Zero; }
        if (_properties != IntPtr.Zero) { Marshal.FreeHGlobal(_properties); _properties = IntPtr.Zero; }
        _session = 0;
        Interlocked.CompareExchange(ref _current, null, this);
    }

    public void Dispose() => StopObserving();

    private static HashSet<IPAddress> LocalAddresses()
    {
        var set = new HashSet<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
                set.Add(a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address);
        return set;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int ProcessTrace(ulong* handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int CloseTrace(ulong traceHandle);
}

/// <summary>Leitura dos eventos do Microsoft-Windows-Kernel-Network (lógica pura, coberta por testes com bytes sintéticos).</summary>
public static class EtwKernelNetwork
{
    public static FlowEvent? Parse(ushort eventId, ReadOnlySpan<byte> data, IReadOnlySet<IPAddress> localAddresses, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(localAddresses);
        (FlowProtocol Proto, bool V6, bool Send)? kind = eventId switch
        {
            10 => (FlowProtocol.Tcp, false, true),
            11 => (FlowProtocol.Tcp, false, false),
            26 => (FlowProtocol.Tcp, true, true),
            27 => (FlowProtocol.Tcp, true, false),
            42 => (FlowProtocol.Udp, false, true),
            43 => (FlowProtocol.Udp, false, false),
            58 => (FlowProtocol.Udp, true, true),
            59 => (FlowProtocol.Udp, true, false),
            _ => null,
        };
        if (kind is not { } k) return null;
        var addrLen = k.V6 ? 16 : 4;
        var need = 8 + 2 * addrLen + 4;
        if (data.Length < need) return null;
        var pid = BinaryPrimitives.ReadInt32LittleEndian(data);
        var size = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var a1 = new IPAddress(data.Slice(8, addrLen));
        var a2 = new IPAddress(data.Slice(8 + addrLen, addrLen));
        var p1 = BinaryPrimitives.ReadUInt16BigEndian(data[(8 + 2 * addrLen)..]);
        var p2 = BinaryPrimitives.ReadUInt16BigEndian(data[(10 + 2 * addrLen)..]);
        if (pid <= 0) return null;
        // daddr/dport é o lado remoto no manifesto; se ele for um endereço local do PC, a ordem está invertida neste evento.
        var (remote, port) = localAddresses.Contains(Normalize(a1)) && !localAddresses.Contains(Normalize(a2)) ? (a2, p2) : (a1, p1);
        remote = Normalize(remote);
        if (IPAddress.IsLoopback(remote) || port == 0) return null;
        return new FlowEvent(pid, k.Proto, remote, port, size, k.Send, at);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
