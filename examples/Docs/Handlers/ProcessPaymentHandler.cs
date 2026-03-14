using Docs.Messages;
using Ratatoskr.Core;

namespace Docs.Handlers;

#region ProcessPaymentHandler
public class ProcessPaymentHandler(ILogger<ProcessPaymentHandler> logger) : IMessageHandler<ProcessPayment>
{
    public Task HandleAsync(ProcessPayment message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing payment of {Amount} for order {OrderId}",
            message.Amount, message.OrderId);
        return Task.CompletedTask;
    }
}
#endregion
