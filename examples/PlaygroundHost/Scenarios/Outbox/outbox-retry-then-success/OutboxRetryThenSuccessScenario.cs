using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess;

public sealed class OutboxRetryThenSuccessScenario : IScenario
{
    public string Slug => "outbox-retry-then-success";

    public string Title => "Outbox relay retries then succeeds";

    public string Description =>
        "Simulates transport failures on the publisher outbox send path for this run, then succeeds; order reaches Fulfilled.";

    public string Topic => "Outbox";

    private static async Task<Guid> StageOrderAsync(
        PublisherDbContext db,
        TimeProvider time,
        string runId,
        CancellationToken cancellationToken)
    {
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
        var orderIdStr = order.Id.ToString();
        var mpPlaced = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        var mpCmd = new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) };
        var mpRes = new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(mpPlaced, runId);
        PlaygroundCorrelation.AttachToMessageProperties(mpCmd, runId);
        PlaygroundCorrelation.AttachToMessageProperties(mpRes, runId);
        db.OutboxMessages.Add(new OutboxRetryThenSuccessOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new OutboxRetryThenSuccessProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new OutboxRetryThenSuccessReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
        return order.Id;
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        var registry = sp.GetRequiredService<OutboxSendFailureRegistry>();
        registry.Register(runId, OutboxSendFailureKind.SucceedAfterNFailures, 2);
        try
        {
            var orderId = await StageOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("staged_with_send_failures");
            return await ScenarioAssertions.OrderEventuallyAsync(
                context.ScopeFactory,
                orderId,
                OrderStatus.Fulfilled,
                TimeSpan.FromSeconds(90),
                time,
                cancellationToken);
        }
        finally
        {
            registry.Unregister(runId);
        }
    }
}
