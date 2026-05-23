using PlaygroundHost.Infrastructure.ScenarioRunning;

namespace PlaygroundHost.Infrastructure;

/// <summary>Per-scenario RabbitMQ object names (slug is the scenario <see cref="IScenario.Slug"/>).</summary>
public static class PlaygroundAmqpNames
{
    public static string ExchangeName(string slug, string purpose) => $"pg.{slug}.{purpose}";

    public static string QueueName(string slug, string purpose) => $"pg.{slug}.{purpose}";

    public static string RetryQueueName(string mainQueueName) => $"{mainQueueName}.retry";

    public static string DlqQueueName(string mainQueueName) => $"{mainQueueName}.dlq";
}
