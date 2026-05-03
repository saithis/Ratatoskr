using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.Outbox.OversizedPayloadRollsBack;

public sealed class OversizedPayloadRollsBackScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "oversized-payload-rolls-back";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues => [];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<OrderPlaced>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Oversized outbox payload";

    public string Description =>
        "Stages an OrderPlaced payload larger than the configured outbox max size; SaveChanges must roll back so the order row is not persisted.";

    public string Topic => "Outbox";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        var now = time.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Status = OrderStatus.Placed,
            CreatedAt = now,
            StatusChangedAt = now,
            PublishOrigin = "outbox",
        };
        db.Orders.Add(order);
        var orderIdStr = order.Id.ToString("D");
        var mp = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(mp, runId);
        db.OutboxMessages.Add(
            new OrderPlaced(orderIdStr, runId, new string('x', 50_000)),
            mp);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new ScenarioVerdict(false, "Expected SaveChanges to fail for oversized payload.");
        }
        catch
        {
            await using var scope2 = context.ScopeFactory.CreateAsyncScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<PublisherDbContext>();
            var orderRowExists = await db2.Orders.AsNoTracking().AnyAsync(o => o.Id == order.Id, cancellationToken);
            return orderRowExists
                ? new ScenarioVerdict(false, "Order row exists after failed save; expected rollback.")
                : new ScenarioVerdict(true);
        }
    }

    [RatatoskrMessage("oversized-payload-rolls-back.order-placed")]
    public sealed record OrderPlaced(
        string OrderId,
        string ScenarioRunId,
        string? BulkPaddingForDemo) : IPlaygroundCorrelatedOrderMessage;
}
