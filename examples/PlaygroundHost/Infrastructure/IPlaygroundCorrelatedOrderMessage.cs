namespace PlaygroundHost.Infrastructure;

/// <summary>Scenario messages that carry correlation for activity recording and inbox/outbox demos.</summary>
public interface IPlaygroundCorrelatedOrderMessage
{
    string OrderId { get; }
    string ScenarioRunId { get; }
}
