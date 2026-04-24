using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotNotifier.Models;

namespace CopilotNotifier.Services;

public static class EventParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static SessionEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var type = root.GetProperty("type").GetString() ?? "";
            var id = root.GetProperty("id").GetString() ?? "";
            var parentId = root.TryGetProperty("parentId", out var pid) ? pid.GetString() : null;
            var timestamp = root.TryGetProperty("timestamp", out var ts)
                ? DateTime.Parse(ts.GetString()!)
                : DateTime.UtcNow;

            SessionEventData? data = null;
            if (root.TryGetProperty("data", out var dataEl))
            {
                data = ParseEventData(dataEl);
            }

            return new SessionEvent(type, id, parentId, timestamp, data);
        }
        catch
        {
            return null;
        }
    }

    private static SessionEventData ParseEventData(JsonElement el)
    {
        string? shutdownType = null, turnId = null, summary = null, cwd = null, newMode = null, previousMode = null;
        SessionResumeContext? context = null;

        if (el.TryGetProperty("shutdownType", out var st)) shutdownType = st.GetString();
        if (el.TryGetProperty("turnId", out var ti)) turnId = ti.GetString();
        if (el.TryGetProperty("summary", out var su)) summary = su.GetString();
        if (el.TryGetProperty("cwd", out var cw)) cwd = cw.GetString();
        if (el.TryGetProperty("newMode", out var nm)) newMode = nm.GetString();
        if (el.TryGetProperty("previousMode", out var pm)) previousMode = pm.GetString();
        string? toolName = null;
        if (el.TryGetProperty("toolName", out var tn)) toolName = tn.GetString();
        string? toolCallId = null;
        if (el.TryGetProperty("toolCallId", out var tci)) toolCallId = tci.GetString();
        if (el.TryGetProperty("context", out var ctx))
        {
            string? ctxCwd = null;
            if (ctx.TryGetProperty("cwd", out var cc)) ctxCwd = cc.GetString();
            context = new SessionResumeContext(ctxCwd);
        }

        return new SessionEventData(shutdownType, turnId, summary, cwd, newMode, previousMode, context, toolName, toolCallId);
    }

    public static bool IsNotificationWorthy(string eventType) =>
        eventType is "assistant.turn_end" or "session.shutdown" or "session.task_complete";

    public static NotificationType? GetNotificationType(string eventType) => eventType switch
    {
        "assistant.turn_end" => NotificationType.TaskComplete,
        "session.shutdown" => NotificationType.SessionCompleted,
        "session.task_complete" => NotificationType.TaskComplete,
        _ => null
    };
}
