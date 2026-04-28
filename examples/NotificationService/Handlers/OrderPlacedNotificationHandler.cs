using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace NotificationService.Handlers;

public class OrderPlacedNotificationHandler(ILogger<OrderPlacedNotificationHandler> logger) : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Notification] Order placed: {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
