namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public sealed record ScenarioStartResult(Guid? RunId, string? Title, string? Error);
