namespace PlaygroundHost.Infrastructure;

public static class PlaygroundRabbitQueues
{
    public static IReadOnlyList<RabbitConsumerQueue> ConsumerQueues { get; } =
    [
        new("orders", "ecommerce.events.orders", 5),
        new("inventory", "ecommerce.commands.inventory", 5),
        new("notifications", "ecommerce.events.notifications", 5),
    ];

    public static string RetryQueueName(string mainQueueName) => $"{mainQueueName}.retry";

    public static string DlqQueueName(string mainQueueName) => $"{mainQueueName}.dlq";

    public sealed record RabbitConsumerQueue(string Key, string MainQueueName, int RetryDelaySeconds);
}
