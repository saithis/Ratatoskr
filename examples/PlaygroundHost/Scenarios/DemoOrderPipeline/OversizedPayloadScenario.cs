using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class OversizedPayloadScenario : IScenario
{
    public string Slug => "oversized-payload-rolls-back";

    public string Title => "Oversized outbox payload";

    public string Description =>
        "Stages an OrderPlaced payload larger than the configured outbox max size; SaveChanges must roll back so the order row is not persisted.";

    public string Topic => "Outbox";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        ScenarioToggleReset.ApplyBaseline(sp);
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
        db.OutboxMessages.Add(
            new OrderPlaced
            {
                OrderId = orderIdStr,
                ScenarioRunId = context.ScenarioRunId,
                BulkPaddingForDemo = new string('x', 50_000),
            },
            new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) });
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
}
