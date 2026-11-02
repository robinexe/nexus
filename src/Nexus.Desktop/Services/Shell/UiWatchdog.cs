using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Nexus.Desktop.Services.Shell;

/// <summary>
/// Diagnóstico permanente e barato: guarda a última etapa da interface (página aberta, módulo lendo) e, se a thread da
/// interface ficar sem responder por mais de 2 s, anota em %LocalAppData%\NEXUS\desktop-diag.log quanto tempo e em qual etapa.
/// Suspensão do PC não conta como travamento: o silêncio é medido com o relógio que exclui o sono
/// (QueryUnbiasedInterruptTime), o aviso do Windows de suspender/retomar zera a contagem e um vigia que ficou ele mesmo sem
/// tiques (processo congelado, ex.: Modern Standby) também — ver <see cref="StallDetector"/>. Não envia nada para lugar nenhum.
/// </summary>
public static class UiWatchdog
{
    private static readonly Lock Gate = new();
    private static string _step = "iniciando";
    private static StallDetector _detector = new(AwakeMs());
    private static Timer? _timer;
    private static readonly Stopwatch SinceStart = Stopwatch.StartNew();

    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "desktop-diag.log");

    public static void Step(string step)
    {
        _step = step;
        Write($"etapa: {step} (+{SinceStart.ElapsedMilliseconds} ms desde o início)");
    }

    /// <summary>
    /// Cronometra uma fase da abertura (na thread da interface): anota "fase: X levou N ms" quando passa de
    /// <paramref name="logAboveMs"/> (0 = sempre). Use com <c>using</c>.
    /// </summary>
    public static IDisposable Phase(string name, long logAboveMs = 0)
    {
        _step = name;
        return new PhaseTimer(name, logAboveMs);
    }

    private sealed class PhaseTimer(string name, long logAboveMs) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var ms = (long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            if (ms >= logAboveMs) Write($"fase: {name} levou {ms} ms");
        }
    }

    public static void Start(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        lock (Gate) _detector = new StallDetector(AwakeMs());
        if (OperatingSystem.IsWindows())
        {
            try { Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged; }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException) { } // sem janela de eventos do sistema: fica só o relógio
        }
        _timer = new Timer(_ =>
        {
            StallEvent e;
            lock (Gate) e = _detector.Tick(Environment.TickCount64, AwakeMs());
            if (e.Kind == StallEventKind.Stalled) Write($"INTERFACE PARADA há {e.Seconds:0.0} s durante: {_step}");
            else if (e.Kind == StallEventKind.ResumedFromSleep) Write($"PC voltou da suspensão ({e.Seconds:0} s parado; não é travamento da interface)");
            dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                double? back;
                lock (Gate) back = _detector.Pong(AwakeMs());
                if (back is { } s) Write($"interface voltou depois de {s:0.0} s (etapa: {_step})");
            });
        }, null, 1000, 1000);
    }

    private static void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            lock (Gate) _detector.ResetAt(AwakeMs());
            Write("PC entrando em suspensão");
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            lock (Gate) _detector.ResetAt(AwakeMs());
            Write("PC voltou da suspensão");
        }
    }

    /// <summary>Milissegundos desde o boot SEM contar suspensão/hibernação (fora do Windows: o relógio comum).</summary>
    private static long AwakeMs()
    {
        if (OperatingSystem.IsWindows() && NativeMethods.QueryUnbiasedInterruptTime(out var t100ns)) return (long)(t100ns / 10_000);
        return Environment.TickCount64;
    }

    private static class NativeMethods
    {
        /// <summary>Tempo de interrupção sem viés: unidades de 100 ns desde o boot, sem contar suspensão/hibernação.</summary>
        [DllImport("kernel32.dll", PreserveSig = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
    }

    private static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 512 * 1024) info.Delete(); // nunca cresce sem limite
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
