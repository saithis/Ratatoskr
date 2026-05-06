namespace PlaygroundHost.Infrastructure;

public sealed record PlaygroundActivityEntry(
    DateTimeOffset Timestamp,
    string Stage,
    string? MessageId,
    string? MessageType,
    string? OrderId,
    string? ScenarioRunId,
    bool? IsSuccess,
    string? Error,
    string? TransportName,
    string? DispatchResult);
