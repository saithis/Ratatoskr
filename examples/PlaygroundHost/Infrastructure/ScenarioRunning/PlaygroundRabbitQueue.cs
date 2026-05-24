using System.Diagnostics.CodeAnalysis;

namespace PlaygroundHost.Infrastructure.ScenarioRunning;

/// <summary>One main Rabbit queue to report in <c>/api/playground/rabbit-depths</c> (retry/DLQ names derived from <see cref="PlaygroundAmqpNames"/>).</summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This holds rabbitmq queue data"
)]
internal readonly record struct PlaygroundRabbitQueue(string Key, string MainQueueName);
