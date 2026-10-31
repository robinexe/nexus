using System.IO;
using System.Windows.Threading;

namespace Nexus.Desktop.Services.Shell;

/// <summary>
/// Diagnóstico permanente e barato: guarda a última etapa da interface (página aberta, módulo lendo) e, se a thread da
/// interface ficar sem responder por mais de 2 s, anota em %LocalAppData%\NEXUS\desktop-diag.log quanto tempo e em qual etapa.
/// Não envia nada para lugar nenhum.
/// </summary>
public static class UiWatchdog
{
    private static string _step = "iniciando";
    private static long _lastPong = Environment.TickCount64;
    private static Timer? _timer;
    private static bool _reported;

    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "desktop-diag.log");

    public static void Step(string step)
    {
        _step = step;
        Write($"etapa: {step}");
    }

    public static void Start(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _timer = new Timer(_ =>
        {
            var silent = Environment.TickCount64 - Interlocked.Read(ref _lastPong);
            if (silent > 2000 && !_reported) { _reported = true; Write($"INTERFACE PARADA há {silent / 1000.0:0.0} s durante: {_step}"); }
            dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                var gap = Environment.TickCount64 - Interlocked.Read(ref _lastPong);
                if (_reported) { Write($"interface voltou depois de {gap / 1000.0:0.0} s (etapa: {_step})"); _reported = false; }
                Interlocked.Exchange(ref _lastPong, Environment.TickCount64);
            });
        }, null, 1000, 1000);
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
