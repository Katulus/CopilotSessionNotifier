namespace CopilotNotifier.Models;

public enum NotificationType
{
    WaitingForInput,
    SessionCompleted,
    TaskComplete
}

public record NotificationItem(
    string SessionId,
    string DisplayName,
    NotificationType Type,
    DateTime Timestamp,
    int? Pid
)
{
    public string Message => Type switch
    {
        NotificationType.WaitingForInput => $"Waiting for your input",
        NotificationType.SessionCompleted => $"Session completed",
        NotificationType.TaskComplete => $"Background task complete",
        _ => "Session event"
    };

    public string Icon => Type switch
    {
        NotificationType.WaitingForInput => "⏳",
        NotificationType.SessionCompleted => "✅",
        NotificationType.TaskComplete => "🔔",
        _ => "📋"
    };
}
