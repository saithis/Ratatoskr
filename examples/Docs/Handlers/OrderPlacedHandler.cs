using Docs.Messages;
using Ratatoskr.Core;

namespace Docs.Handlers;

#region OrderPlacedHandler
public class OrderPlacedHandler(ILogger<OrderPlacedHandler> logger) : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("Order {OrderId} placed for {Email}, total: {Total}",
            message.OrderId, message.CustomerEmail, message.Total);
        return Task.CompletedTask;
    }
}
#endregion
