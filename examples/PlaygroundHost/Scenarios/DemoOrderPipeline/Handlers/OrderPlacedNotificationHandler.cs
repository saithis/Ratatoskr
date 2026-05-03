using PlaygroundHost.Infrastructure;
using PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline.Handlers;

public class OrderPlacedNotificationHandler(
    NotificationPlaygroundState playground,
    ILogger<OrderPlacedNotificationHandler> logger) : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        if (playground.TryConsumeOrderPlacedNotifyFailure())
        {
            logger.LogWarning("[Notification] OrderPlaced fail toggle ON — throwing for Rabbit retry/DLQ, order {OrderId}", message.OrderId);
            throw new InvalidOperationException("Simulated OrderPlaced notification failure (playground toggle).");
        }

        logger.LogInformation("[Notification] Order placed: {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
