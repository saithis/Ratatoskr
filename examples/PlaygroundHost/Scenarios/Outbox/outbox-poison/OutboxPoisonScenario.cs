using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Outbox.OutboxPoison;

public sealed class OutboxPoisonScenario : IScenario
{
    public string Slug => "outbox-poison";

    public string Title => "Outbox poisoned rows";

    public string Description =>
        "Forces publisher outbox transport sends to fail until messages become poisoned for this run.";

    public string Topic => "Outbox";

    private static async Task StageOrderAsync(
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
        db.OutboxMessages.Add(new OutboxPoisonOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new OutboxPoisonProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new OutboxPoisonReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        var registry = sp.GetRequiredService<OutboxSendFailureRegistry>();
        registry.Register(runId, OutboxSendFailureKind.AlwaysFail, 0);
        try
        {
            var before = await PlaygroundSqlMetrics.CountPoisonedOutboxForScenarioRunAsync(db, runId, cancellationToken);
            await StageOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("staged_always_fail_send");

            var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(90);
            while (time.GetUtcNow() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var scope2 = context.ScopeFactory.CreateAsyncScope();
                var db2 = scope2.ServiceProvider.GetRequiredService<PublisherDbContext>();
                var after = await PlaygroundSqlMetrics.CountPoisonedOutboxForScenarioRunAsync(db2, runId, cancellationToken);
                if (after > before)
                    return new ScenarioVerdict(true, details: new { before, after });

                await Task.Delay(800, cancellationToken);
            }

            await using var scope3 = context.ScopeFactory.CreateAsyncScope();
            var db3 = scope3.ServiceProvider.GetRequiredService<PublisherDbContext>();
            var final = await PlaygroundSqlMetrics.CountPoisonedOutboxForScenarioRunAsync(db3, runId, cancellationToken);
            return new ScenarioVerdict(
                false,
                $"Poisoned outbox count did not increase within timeout (before={before}, after={final}).");
        }
        finally
        {
            registry.Unregister(runId);
        }
    }
}
