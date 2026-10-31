using System.Collections.Concurrent;

namespace Nexus.Security.Scanner;

/// <summary>
/// Verificação "ao vivo" de uma pasta (Downloads) enquanto a página está aberta: arquivos novos ou renomeados são
/// verificados depois de alguns segundos parados (o navegador escreve em partes e renomeia no fim). Só leitura.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly string[] PartialExtensions = [".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload"];
    private readonly FileSystemWatcher _watcher;
    private readonly ScanEngine _engine;
    private readonly Action<FileVerdict> _onResult;
    private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly TimeSpan _settle;
    private int _busy;

    public FolderWatcher(string folder, ScanEngine engine, Action<FileVerdict> onResult, TimeSpan? settle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _onResult = onResult ?? throw new ArgumentNullException(nameof(onResult));
        _settle = settle ?? TimeSpan.FromSeconds(3);
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Touch(e.FullPath);
        _watcher.Changed += (_, e) => Touch(e.FullPath);
        _watcher.Renamed += (_, e) => Touch(e.FullPath);
        _timer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _watcher.EnableRaisingEvents = true;
    }

    public string Folder => _watcher.Path;

    private void Touch(string path)
    {
        if (PartialExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase))) return;
        _pending[path] = DateTime.UtcNow;
    }

    private void Flush()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            foreach (var (path, at) in _pending.ToArray())
            {
                if (DateTime.UtcNow - at < _settle) continue;
                if (!_pending.TryRemove(new KeyValuePair<string, DateTime>(path, at))) continue;
                if (!File.Exists(path)) continue;
                var v = _engine.ScanFile(path);
                if (v is not null) _onResult(v);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
