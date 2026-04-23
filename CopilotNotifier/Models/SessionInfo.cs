namespace CopilotNotifier.Models;

public class SessionInfo
{
    public string Id { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Cwd { get; set; }
    public int? Pid { get; set; }
    public long LastReadPosition { get; set; }
    public DateTime LastEventTime { get; set; }
    public bool IsShutdown { get; set; }
    public string? LastNotifiedEventId { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Summary) ? Summary
        : !string.IsNullOrWhiteSpace(Cwd) ? System.IO.Path.GetFileName(Cwd)
        : Id[..8];
}
