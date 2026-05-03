using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq;

public sealed class DirectConsumeDlqOrderPlacedNotifyHandler(ILogger<DirectConsumeDlqOrderPlacedNotifyHandler> _) : IMessageHandler<DirectConsumeDlqOrderPlaced>
{
    public Task HandleAsync(DirectConsumeDlqOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Simulated OrderPlaced notification failure (DLQ scenario).");
    }
}

public sealed class DirectConsumeDlqOrderPlacedAnalyticsHandler(ILogger<DirectConsumeDlqOrderPlacedAnalyticsHandler> _) : IMessageHandler<DirectConsumeDlqOrderPlaced>
{
    public Task HandleAsync(DirectConsumeDlqOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
