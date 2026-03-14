using Docs.Messages;
using Ratatoskr.Core;

namespace Docs.Handlers;

#region ShipOrderHandler
public class ShipOrderHandler(ILogger<ShipOrderHandler> logger) : IMessageHandler<ShipOrder>
{
    public Task HandleAsync(ShipOrder message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("Shipping order {OrderId} to {Address}",
            message.OrderId, message.ShippingAddress);
        return Task.CompletedTask;
    }
}
#endregion
