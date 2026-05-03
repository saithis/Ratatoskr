using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess;

public sealed class DirectConsumeSuccessScenario : IScenario
{
    public string Slug => "direct-consume-success";

    public string Title => "Direct publish happy path";

    public string Description =>
        "Persists the order then publishes Rabbit-bound messages with PublishDirectAsync (no outbox); expects Fulfilled.";

    public string Topic => "Direct consume";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var bus = sp.GetRequiredService<IRatatoskr>();
        var runId = context.ScenarioRunId;
        var now = time.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Status = OrderStatus.Placed,
            CreatedAt = now,
            StatusChangedAt = now,
            PublishOrigin = "direct",
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        var orderIdStr = order.Id.ToString();
        var p1 = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        var p2 = new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) };
        var p3 = new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(p1, runId);
        PlaygroundCorrelation.AttachToMessageProperties(p2, runId);
        PlaygroundCorrelation.AttachToMessageProperties(p3, runId);
        await bus.PublishDirectAsync(new DirectConsumeSuccessOrderPlaced(orderIdStr, runId), p1, cancellationToken);
        await bus.PublishDirectAsync(new DirectConsumeSuccessProcessOrderCommand(orderIdStr, runId), p2, cancellationToken);
        await bus.PublishDirectAsync(new DirectConsumeSuccessReserveStockInternal(orderIdStr, runId), p3, cancellationToken);
        context.StepsCompleted.Add("direct_publish_three_messages");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            order.Id,
            OrderStatus.Fulfilled,
            TimeSpan.FromSeconds(90),
            time,
            cancellationToken);
    }
}
