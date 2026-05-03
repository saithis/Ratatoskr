namespace PlaygroundHost.Infrastructure;

public sealed class PlaygroundToggleRequest
{
    public required string Key { get; init; }
    public string? Mode { get; init; }
    public int? FailureCount { get; init; }
}
