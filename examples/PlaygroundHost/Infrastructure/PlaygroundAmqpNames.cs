namespace PlaygroundHost.Infrastructure;

/// <summary>Per-scenario RabbitMQ object names (slug is the scenario <see cref="IScenario.Slug"/>).</summary>
public static class PlaygroundAmqpNames
{
    public static string EventsExchange(string slug) => $"pg.{slug}.events";

    public static string CommandsExchange(string slug) => $"pg.{slug}.commands";

    public static string OrdersQueue(string slug) => $"pg.{slug}.orders";

    public static string InventoryQueue(string slug) => $"pg.{slug}.inventory";

    public static string NotificationsQueue(string slug) => $"pg.{slug}.notifications";

    public static string ReplayDedupInboxQueue(string slug) => $"pg.{slug}.replay-inbox";

    public static string ReplayDedupDirectQueue(string slug) => $"pg.{slug}.replay-direct";

    public static string ExchangeName(string slug, string purpose) => $"pg.{slug}.{purpose}";
    public static string QueueName(string slug, string purpose) => $"pg.{slug}.{purpose}";
    
    public static string RetryQueueName(string mainQueueName) => $"{mainQueueName}.retry";

    public static string DlqQueueName(string mainQueueName) => $"{mainQueueName}.dlq";
}
