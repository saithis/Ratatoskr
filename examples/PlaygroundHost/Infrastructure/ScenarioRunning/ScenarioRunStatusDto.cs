namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public sealed record ScenarioRunStatusDto(
    Guid Id,
    string ScenarioSlug,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Detail);
