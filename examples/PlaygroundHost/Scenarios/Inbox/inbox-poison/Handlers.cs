using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Persistence;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Inbox.InboxPoison;

public sealed class InboxPoisonReserveStockInternalHandler(ILogger<InboxPoisonReserveStockInternalHandler> logger)
    : IMessageHandler<InboxPoisonReserveStockInternal>
{
    public Task HandleAsync(InboxPoisonReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class InboxPoisonProcessOrderHandler(ILogger<InboxPoisonProcessOrderHandler> logger)
    : IMessageHandler<InboxPoisonProcessOrderCommand>
{
    public Task HandleAsync(InboxPoisonProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("Simulated inventory inbox failure (poison scenario)."));
}

public sealed class InboxPoisonOrderPlacedNotifyHandler(ILogger<InboxPoisonOrderPlacedNotifyHandler> logger) : IMessageHandler<InboxPoisonOrderPlaced>
{
    public Task HandleAsync(InboxPoisonOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class InboxPoisonOrderPlacedAnalyticsHandler(ILogger<InboxPoisonOrderPlacedAnalyticsHandler> logger) : IMessageHandler<InboxPoisonOrderPlaced>
{
    public Task HandleAsync(InboxPoisonOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
