using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace NotificationService.Handlers;

public class OrderFulfilledNotificationHandler(ILogger<OrderFulfilledNotificationHandler> logger) : IMessageHandler<OrderFulfilled>
{
    public Task HandleAsync(OrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Notification] Order fulfilled: {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
