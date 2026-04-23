using System.IO;
using System.Windows.Threading;

namespace CopilotNotifier.Services;

public class SessionWatcher : IDisposable
{
    private readonly string _sessionStatePath;
    private readonly SessionStateTracker _tracker;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _pollTimer;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<string> _pendingChanges = new();
    private System.Threading.Timer? _debounceTimer;

    public SessionWatcher(string sessionStatePath, SessionStateTracker tracker, Dispatcher dispatcher)
    {
        _sessionStatePath = sessionStatePath;
        _tracker = tracker;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (!Directory.Exists(_sessionStatePath))
        {
            Directory.CreateDirectory(_sessionStatePath);
        }

        _watcher = new FileSystemWatcher(_sessionStatePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter = "events.jsonl",
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;

        // Debounce timer fires on thread pool, avoiding UI thread
        _debounceTimer = new System.Threading.Timer(OnDebounceFlush, null, Timeout.Infinite, Timeout.Infinite);

        _pollTimer = new System.Threading.Timer(_ =>
        {
            try { _tracker.ScanAllSessions(); }
            catch { /* swallow, retry next tick */ }
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var sessionDir = Path.GetDirectoryName(e.FullPath);
        if (sessionDir == null) return;

        lock (_pendingChanges)
        {
            _pendingChanges.Add(sessionDir);
        }

        // Reset debounce timer
        _debounceTimer?.Change(500, Timeout.Infinite);
    }

    private void OnDebounceFlush(object? state)
    {
        string[] dirs;
        lock (_pendingChanges)
        {
            dirs = _pendingChanges.ToArray();
            _pendingChanges.Clear();
        }

        foreach (var dir in dirs)
        {
            try { _tracker.ProcessSessionDirectory(dir); }
            catch { /* swallow, retry next poll */ }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _pollTimer?.Dispose();
        _debounceTimer?.Dispose();
    }
}
