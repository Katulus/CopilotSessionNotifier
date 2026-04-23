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

                if (_initialScanComplete && EventParser.IsNotificationWorthy(evt.Type))
                {
                    var notifType = EventParser.GetNotificationType(evt.Type);
                    if (notifType.HasValue && _notifiedEventIds.Add(evt.Id))
                    {
                        session.LastNotifiedEventId = evt.Id;
                        UpdateSessionMetadata(session, sessionDir);
                        NotificationReady?.Invoke(new NotificationItem(
                            session.Id,
                            session.DisplayName,
                            notifType.Value,
                            evt.Timestamp,
                            session.Pid
                        ));
                    }
                }
            }
        }
        catch (IOException)
        {
            // File is being written to, will retry on next poll
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

        // Read lock file for PID
        try
        {
            var lockFiles = Directory.GetFiles(sessionDir, "inuse.*.lock");
            if (lockFiles.Length > 0)
            {
                var pidStr = File.ReadAllText(lockFiles[0]).Trim();
                if (int.TryParse(pidStr, out var pid))
                    session.Pid = pid;
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
