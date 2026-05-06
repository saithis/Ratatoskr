namespace PlaygroundHost.Persistence;

public class PlaygroundRunEntity
{
    public Guid Id { get; set; }
    public string ScenarioSlug { get; set; } = "";
    public string State { get; set; } = "Queued";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Detail { get; set; }
    public int StepIndex { get; set; }
    public string? CurrentStep { get; set; }
    public bool CancelRequested { get; set; }
}
