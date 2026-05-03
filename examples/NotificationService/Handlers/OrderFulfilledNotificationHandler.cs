using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace NotificationService.Handlers;

public class OrderFulfilledNotificationHandler(
    NotificationPlaygroundState playground,
    ILogger<OrderFulfilledNotificationHandler> logger) : IMessageHandler<OrderFulfilled>
{
    public Task HandleAsync(OrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        if (playground.TryConsumeOrderFulfilledNotifyFailure())
        {
            logger.LogWarning("[Notification] OrderFulfilled fail toggle ON — throwing for Rabbit retry/DLQ, order {OrderId}", message.OrderId);
            throw new InvalidOperationException("Simulated OrderFulfilled notification failure (playground toggle).");
        }

        logger.LogInformation("[Notification] Order fulfilled: {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
