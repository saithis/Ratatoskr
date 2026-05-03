using PlaygroundHost.Infrastructure;
using PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline.Handlers;

public class OrderPlacedAnalyticsHandler(
    NotificationPlaygroundState playground,
    ILogger<OrderPlacedAnalyticsHandler> logger) : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        if (playground.TryConsumeOrderPlacedAnalyticsFailure())
        {
            logger.LogWarning(
                "[Notification analytics] OrderPlaced fail toggle ON — throwing for Rabbit retry/DLQ, order {OrderId}",
                message.OrderId);
            throw new InvalidOperationException("Simulated OrderPlaced analytics failure (playground toggle).");
        }

        logger.LogInformation("[Notification analytics] Order placed: {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
