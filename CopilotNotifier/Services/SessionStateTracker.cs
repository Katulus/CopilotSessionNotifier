using System.IO;
using CopilotNotifier.Models;

namespace CopilotNotifier.Services;

public class SessionStateTracker
{
    private readonly Dictionary<string, SessionInfo> _sessions = new();
    private readonly string _sessionStatePath;
    private bool _initialScanComplete;
    private readonly object _lock = new();
    private readonly HashSet<string> _notifiedEventIds = new();
    
    // Debounce: pending turn_end notifications wait before firing
    private readonly Dictionary<string, System.Threading.Timer> _pendingTurnEndTimers = new();
    private readonly Dictionary<string, (NotificationItem item, string eventId)> _pendingTurnEndData = new();
    private const int TurnEndDebounceMs = 5000;
    
    // Suppress turn_end after task_complete (they always come as a pair)
    private readonly HashSet<string> _suppressNextTurnEnd = new();

    public SessionStateTracker(string sessionStatePath)
    {
        _sessionStatePath = sessionStatePath;
    }

    public event Action<NotificationItem>? NotificationReady;

    public IReadOnlyDictionary<string, SessionInfo> Sessions
    {
        get { lock (_lock) { return new Dictionary<string, SessionInfo>(_sessions); } }
    }

    public void PerformInitialScan()
    {
        if (!Directory.Exists(_sessionStatePath))
            return;

        lock (_lock)
        {
            foreach (var dir in Directory.GetDirectories(_sessionStatePath))
            {
                var sessionId = Path.GetFileName(dir);
                var eventsFile = Path.Combine(dir, "events.jsonl");
                if (!File.Exists(eventsFile))
                    continue;

                var session = GetOrCreateSession(sessionId, dir);
                using var fs = new FileStream(eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                session.LastReadPosition = fs.Length;

                UpdateSessionMetadata(session, dir);
            }

            _initialScanComplete = true;
        }
    }

    public void ProcessSessionDirectory(string sessionDir)
    {
        var sessionId = Path.GetFileName(sessionDir);
        var eventsFile = Path.Combine(sessionDir, "events.jsonl");
        if (!File.Exists(eventsFile))
            return;

        lock (_lock)
        {
            var session = GetOrCreateSession(sessionId, sessionDir);
            if (session.IsShutdown)
                return;

            ReadNewEvents(session, eventsFile, sessionDir);
        }
    }

    public void ScanAllSessions()
    {
        if (!Directory.Exists(_sessionStatePath))
            return;

        foreach (var dir in Directory.GetDirectories(_sessionStatePath))
        {
            ProcessSessionDirectory(dir);
        }
    }

    private void ReadNewEvents(SessionInfo session, string eventsFile, string sessionDir)
    {
        try
        {
            using var fs = new FileStream(eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Handle file truncation/recreation
            if (fs.Length < session.LastReadPosition)
                session.LastReadPosition = 0;

            if (fs.Length <= session.LastReadPosition)
                return;

            fs.Position = session.LastReadPosition;
            var buffer = new byte[fs.Length - session.LastReadPosition];
            var bytesRead = fs.Read(buffer, 0, buffer.Length);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // Only process complete lines (up to last newline)
            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
                return; // No complete lines yet

            var completeText = text[..(lastNewline + 1)];
            session.LastReadPosition += System.Text.Encoding.UTF8.GetByteCount(completeText);

            foreach (var line in completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var evt = EventParser.ParseLine(line);
                if (evt == null) continue;

                UpdateSessionFromEvent(session, evt, sessionDir);

                if (!_initialScanComplete)
                    continue;

                // If we see a turn_start, cancel any pending turn_end notification for this session
                if (evt.Type == "assistant.turn_start")
                {
                    CancelPendingTurnEnd(session.Id);
                    continue;
                }

                if (EventParser.IsNotificationWorthy(evt.Type))
                {
                    var notifType = EventParser.GetNotificationType(evt.Type);
                    if (notifType.HasValue && _notifiedEventIds.Add(evt.Id))
                    {
                        // Suppress turn_end that immediately follows task_complete
                        if (evt.Type == "assistant.turn_end" && _suppressNextTurnEnd.Remove(session.Id))
                        {
                            _notifiedEventIds.Remove(evt.Id);
                            continue;
                        }

                        // Mark that the next turn_end for this session should be suppressed
                        if (evt.Type == "session.task_complete")
                        {
                            _suppressNextTurnEnd.Add(session.Id);
                        }
                        session.LastNotifiedEventId = evt.Id;
                        UpdateSessionMetadata(session, sessionDir);

                        var item = new NotificationItem(
                            session.Id,
                            session.DisplayName,
                            notifType.Value,
                            evt.Timestamp,
                            session.Pid
                        );

                        if (evt.Type == "assistant.turn_end")
                        {
                            // Debounce: wait before notifying, cancel if turn_start follows
                            SchedulePendingTurnEnd(session.Id, item, evt.Id);
                        }
                        else
                        {
                            // Immediate notification for shutdown, task_complete
                            NotificationReady?.Invoke(item);
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            // File is being written to, will retry on next poll
        }
    }

    private void SchedulePendingTurnEnd(string sessionId, NotificationItem item, string eventId)
    {
        // Cancel any existing pending notification for this session
        CancelPendingTurnEnd(sessionId);

        _pendingTurnEndData[sessionId] = (item, eventId);
        _pendingTurnEndTimers[sessionId] = new System.Threading.Timer(
            _ => FirePendingTurnEnd(sessionId),
            null,
            TurnEndDebounceMs,
            System.Threading.Timeout.Infinite
        );
    }

    private void CancelPendingTurnEnd(string sessionId)
    {
        if (_pendingTurnEndTimers.TryGetValue(sessionId, out var timer))
        {
            timer.Dispose();
            _pendingTurnEndTimers.Remove(sessionId);
        }
        if (_pendingTurnEndData.TryGetValue(sessionId, out var data))
        {
            // Remove the event ID from notified set so it doesn't block future dedup
            _notifiedEventIds.Remove(data.eventId);
            _pendingTurnEndData.Remove(sessionId);
        }
    }

    private void FirePendingTurnEnd(string sessionId)
    {
        NotificationItem? item = null;
        lock (_lock)
        {
            if (_pendingTurnEndData.TryGetValue(sessionId, out var data))
            {
                item = data.item;
                _pendingTurnEndData.Remove(sessionId);
            }
            if (_pendingTurnEndTimers.TryGetValue(sessionId, out var timer))
            {
                timer.Dispose();
                _pendingTurnEndTimers.Remove(sessionId);
            }
        }

        if (item != null)
        {
            NotificationReady?.Invoke(item);
        }
    }

    private void UpdateSessionFromEvent(SessionInfo session, SessionEvent evt, string sessionDir)
    {
        session.LastEventTime = evt.Timestamp;

        switch (evt.Type)
        {
            case "session.start":
                break;
            case "session.resume":
                if (evt.Data?.Context?.Cwd != null)
                    session.Cwd = evt.Data.Context.Cwd;
                break;
            case "session.shutdown":
                session.IsShutdown = true;
                // Cancel any pending turn_end when session shuts down
                CancelPendingTurnEnd(session.Id);
                break;
        }
    }

    private void UpdateSessionMetadata(SessionInfo session, string sessionDir)
    {
        // Read workspace.yaml
        var workspaceFile = Path.Combine(sessionDir, "workspace.yaml");
        if (File.Exists(workspaceFile))
        {
            try
            {
                var lines = File.ReadAllLines(workspaceFile);
                foreach (var line in lines)
                {
                    if (line.StartsWith("summary:") && !line.StartsWith("summary_count:"))
                    {
                        var val = line["summary:".Length..].Trim();
                        if (!string.IsNullOrEmpty(val))
                            session.Summary = val;
                    }
                    else if (line.StartsWith("cwd:"))
                    {
                        var val = line["cwd:".Length..].Trim();
                        if (!string.IsNullOrEmpty(val))
                            session.Cwd = val;
                    }
                }
            }
            catch (IOException) { }
        }

        // Read lock file for PID — only set if process is actually alive
        try
        {
            var lockFiles = Directory.GetFiles(sessionDir, "inuse.*.lock");
            if (lockFiles.Length > 0)
            {
                var fileName = Path.GetFileName(lockFiles[0]);
                var parts = fileName.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var pid))
                {
                    try
                    {
                        System.Diagnostics.Process.GetProcessById(pid);
                        session.Pid = pid;
                    }
                    catch (ArgumentException)
                    {
                        // Process is dead — stale lock file
                        session.Pid = null;
                    }
                }
            }
            else
            {
                session.Pid = null;
            }
        }
        catch (IOException) { }
    }

    private SessionInfo GetOrCreateSession(string sessionId, string sessionDir)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            session = new SessionInfo { Id = sessionId };
            UpdateSessionMetadata(session, sessionDir);
            _sessions[sessionId] = session;
        }
        return session;
    }
}
