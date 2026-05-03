using NotificationService;
using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace NotificationService.Handlers;

public class OrderPlacedNotificationHandler(
    NotificationFailureState failureState,
    ILogger<OrderPlacedNotificationHandler> logger) : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        if (failureState.IsEnabled)
        {
            logger.LogWarning("[Notification] Failure mode ON — throwing for Rabbit retry/DLQ demo, order {OrderId}", message.OrderId);
            throw new InvalidOperationException("Simulated notification failure (failure mode is ON).");
        }

        logger.LogInformation("[Notification] Order placed: {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
