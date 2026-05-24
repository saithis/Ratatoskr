namespace PlaygroundHost.Infrastructure.ScenarioRunning;

/// <summary>One main Rabbit queue to report in <c>/api/playground/rabbit-depths</c> (retry/DLQ names derived from <see cref="PlaygroundAmqpNames"/>).</summary>
public readonly record struct PlaygroundRabbitDepthQueue(string Key, string MainQueueName);
