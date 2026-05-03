using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Inbox.InboxPoison;

public sealed class InboxPoisonScenario : IScenario
{
    public string Slug => "inbox-poison";

    public string Title => "Inventory inbox poison";

    public string Description =>
        "Inventory command handler throws until a poisoned inbox row appears for this run.";

    public string Topic => "Inbox";

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
        db.OutboxMessages.Add(new InboxPoisonOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new InboxPoisonProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new InboxPoisonReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var pub = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        int before;
        await using (var conScope = context.ScopeFactory.CreateAsyncScope())
        {
            var conDb = conScope.ServiceProvider.GetRequiredService<ConsumerDbContext>();
            before = await PlaygroundSqlMetrics.CountPoisonedInboxForScenarioRunAsync(conDb, runId, cancellationToken);
        }

        await StageOrderAsync(pub, time, runId, cancellationToken);
        context.StepsCompleted.Add("inventory_throw_mode");

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(90);
        while (time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope2 = context.ScopeFactory.CreateAsyncScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ConsumerDbContext>();
            var after = await PlaygroundSqlMetrics.CountPoisonedInboxForScenarioRunAsync(db2, runId, cancellationToken);
            if (after > before)
                return new ScenarioVerdict(true, details: new { before, after });

            await Task.Delay(800, cancellationToken);
        }

        await using var conFinal = context.ScopeFactory.CreateAsyncScope();
        var conDbFinal = conFinal.ServiceProvider.GetRequiredService<ConsumerDbContext>();
        var final = await PlaygroundSqlMetrics.CountPoisonedInboxForScenarioRunAsync(conDbFinal, runId, cancellationToken);
        return new ScenarioVerdict(
            false,
            $"Poisoned consumer inbox count did not increase (before={before}, after={final}).");
    }
}
