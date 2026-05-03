namespace PlaygroundMessages;

/// <summary>
/// Consumer queue names and retry delay used by the e-commerce playground (must match service registrations).
/// </summary>
public static class PlaygroundRabbitQueues
{
    public sealed record ConsumerQueueInfo(string Key, string MainQueueName, int RetryDelaySeconds);

    public static readonly IReadOnlyList<ConsumerQueueInfo> ConsumerQueues =
    [
        new("orders-events", "ecommerce.events.orders", 5),
        new("inventory-commands", "ecommerce.commands.inventory", 5),
        new("notifications-events", "ecommerce.events.notifications", 5),
    ];

    public static string RetryQueueName(string mainQueueName) => $"{mainQueueName}.retry";

    public static string DlqQueueName(string mainQueueName) => $"{mainQueueName}.dlq";
}
