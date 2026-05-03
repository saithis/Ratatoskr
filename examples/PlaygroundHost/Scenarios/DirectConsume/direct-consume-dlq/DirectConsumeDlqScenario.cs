using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq;

public sealed class DirectConsumeDlqScenario : IScenario
{
    public string Slug => "direct-consume-dlq";

    public string Title => "Notification DLQ (no inbox)";

    public string Description =>
        "OrderPlaced notify handler always fails; expect this scenario's notifications DLQ depth to grow.";

    public string Topic => "Direct consume";

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
        db.OutboxMessages.Add(new DirectConsumeDlqOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new DirectConsumeDlqProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new DirectConsumeDlqReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var cfg = sp.GetRequiredService<IConfiguration>();
        var rabbitCs = cfg.GetConnectionString("rabbitmq")
            ?? throw new InvalidOperationException("rabbitmq connection string missing.");
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        const string slug = "direct-consume-dlq";
        var mainQ = PlaygroundAmqpNames.NotificationsQueue(slug);
        var d0 = await RabbitDlqDepthReader.GetDlqCountAsync(rabbitCs, mainQ, cancellationToken);
        await StageOrderAsync(db, time, runId, cancellationToken);
        context.StepsCompleted.Add("notification_always_fail");

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(90);
        while (time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var d = await RabbitDlqDepthReader.GetDlqCountAsync(rabbitCs, mainQ, cancellationToken);
            if (d > d0)
                return new ScenarioVerdict(true, details: new { before = d0, after = d });

            await Task.Delay(1000, cancellationToken);
        }

        var final = await RabbitDlqDepthReader.GetDlqCountAsync(rabbitCs, mainQ, cancellationToken);
        return new ScenarioVerdict(false, $"DLQ depth did not increase (before={d0}, after={final}).");
    }
}
