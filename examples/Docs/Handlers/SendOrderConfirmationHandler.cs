using Docs.Messages;
using Ratatoskr.Core;

namespace Docs.Handlers;

#region SendOrderConfirmationHandler
public class SendOrderConfirmationHandler(ILogger<SendOrderConfirmationHandler> logger)
    : IMessageHandler<SendOrderConfirmation>
{
    public Task HandleAsync(SendOrderConfirmation message, MessageProperties properties,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sending order confirmation for {OrderId} to {Email}",
            message.OrderId, message.CustomerEmail);
        return Task.CompletedTask;
    }
}
#endregion
