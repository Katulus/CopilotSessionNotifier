namespace CopilotNotifier.Models;

public record SessionEvent(
    string Type,
    string Id,
    string? ParentId,
    DateTime Timestamp,
    SessionEventData? Data
);

public record SessionEventData(
    string? ShutdownType = null,
    string? TurnId = null,
    string? Summary = null,
    string? Cwd = null,
    string? NewMode = null,
    string? PreviousMode = null,
    SessionResumeContext? Context = null
);

public record SessionResumeContext(
    string? Cwd = null
);
