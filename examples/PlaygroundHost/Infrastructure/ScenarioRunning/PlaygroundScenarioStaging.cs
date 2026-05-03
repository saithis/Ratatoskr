using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure.ScenarioRunning;

/// <summary>Shared helpers for staging publisher orders and correlated outbox payloads.</summary>
public static class PlaygroundScenarioStaging
{
    public static Order AddPlacedOrderToContext(PublisherDbContext db, TimeProvider time, string publishOrigin)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Status = OrderStatus.Placed,
            CreatedAt = now,
            StatusChangedAt = now,
            PublishOrigin = publishOrigin,
        };
        db.Orders.Add(order);
        return order;
    }

    public static void StageCorrelatedOutboxMessage<TMessage>(
        PublisherDbContext db,
        string scenarioRunId,
        TMessage message,
        string cloudEventsMessageId)
        where TMessage : notnull
    {
        var props = new MessageProperties { Id = cloudEventsMessageId };
        PlaygroundCorrelation.AttachToMessageProperties(props, scenarioRunId);
        db.OutboxMessages.Add(message, props);
    }
}
