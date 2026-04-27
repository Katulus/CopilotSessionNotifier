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

    // Track previous event type per session for deterministic idle detection
    private readonly Dictionary<string, string> _lastEventType = new();

    // Suppress turn_end after task_complete (they always come as a pair)
    private readonly HashSet<string> _suppressNextTurnEnd = new();

    // Tools that typically require user approval before execution.
    private static readonly HashSet<string> ApprovableTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "bash", "shell", "create", "edit", "write"
    };

    // Configurable at runtime via App settings. 0 or negative disables detection.
    public bool ApprovalDetectionEnabled { get; set; } = true;
    public TimeSpan ApprovalPendingDelay { get; set; } = TimeSpan.FromSeconds(2);

    // Pending-approval timers keyed by toolCallId. If the paired
    // tool.execution_complete / abort arrives before the timer fires, the timer
    // is cancelled; otherwise we treat it as "waiting for approval".
    private readonly Dictionary<string, System.Threading.Timer> _pendingApprovalTimers = new();
    private readonly Dictionary<string, HashSet<string>> _sessionPendingApprovals = new();

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

            if (fs.Length < session.LastReadPosition)
                session.LastReadPosition = 0;

            if (fs.Length <= session.LastReadPosition)
                return;

            fs.Position = session.LastReadPosition;
            var buffer = new byte[fs.Length - session.LastReadPosition];
            var bytesRead = fs.Read(buffer, 0, buffer.Length);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);

            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
                return;

            var completeText = text[..(lastNewline + 1)];
            session.LastReadPosition += System.Text.Encoding.UTF8.GetByteCount(completeText);

            foreach (var line in completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var evt = EventParser.ParseLine(line);
                if (evt == null) continue;

                // Get previous event type BEFORE updating
                _lastEventType.TryGetValue(session.Id, out var prevEventType);
                _lastEventType[session.Id] = evt.Type;

                UpdateSessionFromEvent(session, evt, sessionDir);

                if (!_initialScanComplete)
                    continue;

                // Detect tools that block waiting for user response
                if (evt.Type == "tool.execution_start" &&
                    evt.Data?.ToolName is "ask_user" or "exit_plan_mode" &&
                    _notifiedEventIds.Add(evt.Id))
                {
                    // Suppress the turn_end that follows after user answers
                    _suppressNextTurnEnd.Add(session.Id);

                    session.LastNotifiedEventId = evt.Id;
                    UpdateSessionMetadata(session, sessionDir);

                    NotificationReady?.Invoke(new NotificationItem(
                        session.Id,
                        session.DisplayName,
                        NotificationType.WaitingForInput,
                        evt.Timestamp,
                        session.Pid
                    ));
                    continue;
                }

                // Detect approvable tool starts — if unresolved within a short delay,
                // treat as "waiting for approval" and emit a WaitingForInput notification.
                if (ApprovalDetectionEnabled &&
                    ApprovalPendingDelay > TimeSpan.Zero &&
                    evt.Type == "tool.execution_start" &&
                    evt.Data?.ToolName != null &&
                    evt.Data?.ToolCallId != null &&
                    ApprovableTools.Contains(evt.Data.ToolName))
                {
                    ScheduleApprovalCheck(session, sessionDir, evt);
                }

                // Resolve any pending approval when the tool completes.
                if ((evt.Type == "tool.execution_complete" || evt.Type == "abort") &&
                    evt.Data?.ToolCallId != null)
                {
                    CancelApprovalCheck(session.Id, evt.Data.ToolCallId);
                }

                // An abort resolves all pending approvals for the session.
                if (evt.Type == "abort")
                {
                    CancelAllApprovalChecks(session.Id);
                }

                if (EventParser.IsNotificationWorthy(evt.Type))
                {
                    var notifType = EventParser.GetNotificationType(evt.Type);
                    if (notifType.HasValue && _notifiedEventIds.Add(evt.Id))
                    {
                        if (evt.Type == "assistant.turn_end")
                        {
                            // Suppress turn_end after task_complete
                            if (_suppressNextTurnEnd.Remove(session.Id))
                            {
                                _notifiedEventIds.Remove(evt.Id);
                                continue;
                            }

                            // Only notify if the previous event was assistant.message
                            // (assistant finished with text = truly waiting for user input)
                            // If previous was tool.execution_complete, another turn starts immediately
                            if (prevEventType != "assistant.message")
                            {
                                _notifiedEventIds.Remove(evt.Id);
                                continue;
                            }
                        }

                        if (evt.Type == "session.task_complete")
                        {
                            _suppressNextTurnEnd.Add(session.Id);
                        }

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
        catch (IOException) { }
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
                CancelAllApprovalChecks(session.Id);
                break;
        }
    }

    private void UpdateSessionMetadata(SessionInfo session, string sessionDir)
    {
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

    private void ScheduleApprovalCheck(SessionInfo session, string sessionDir, SessionEvent evt)
    {
        var toolCallId = evt.Data!.ToolCallId!;
        if (_pendingApprovalTimers.ContainsKey(toolCallId))
            return;

        if (!_sessionPendingApprovals.TryGetValue(session.Id, out var set))
        {
            set = new HashSet<string>();
            _sessionPendingApprovals[session.Id] = set;
        }
        set.Add(toolCallId);

        var eventId = evt.Id;
        var timestamp = evt.Timestamp;
        var sessionId = session.Id;

        var timer = new System.Threading.Timer(_ =>
        {
            NotificationItem? item = null;
            lock (_lock)
            {
                if (!_pendingApprovalTimers.Remove(toolCallId))
                    return;
                if (_sessionPendingApprovals.TryGetValue(sessionId, out var s))
                    s.Remove(toolCallId);

                if (!_sessions.TryGetValue(sessionId, out var sess) || sess.IsShutdown)
                    return;
                if (!_notifiedEventIds.Add(eventId))
                    return;

                sess.LastNotifiedEventId = eventId;
                UpdateSessionMetadata(sess, sessionDir);

                item = new NotificationItem(
                    sess.Id,
                    sess.DisplayName,
                    NotificationType.WaitingForInput,
                    timestamp,
                    sess.Pid
                );
            }

            if (item != null)
                NotificationReady?.Invoke(item);
        }, null, ApprovalPendingDelay, Timeout.InfiniteTimeSpan);

        _pendingApprovalTimers[toolCallId] = timer;
    }

    private void CancelApprovalCheck(string sessionId, string toolCallId)
    {
        if (_pendingApprovalTimers.Remove(toolCallId, out var timer))
        {
            timer.Dispose();
            if (_sessionPendingApprovals.TryGetValue(sessionId, out var s))
                s.Remove(toolCallId);
        }
    }

    private void CancelAllApprovalChecks(string sessionId)
    {
        if (!_sessionPendingApprovals.TryGetValue(sessionId, out var set))
            return;
        foreach (var id in set.ToArray())
        {
            if (_pendingApprovalTimers.Remove(id, out var timer))
                timer.Dispose();
        }
        set.Clear();
    }
}
