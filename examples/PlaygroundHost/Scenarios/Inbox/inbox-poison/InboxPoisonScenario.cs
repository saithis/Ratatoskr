using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.Inbox.InboxPoison;

public sealed class InboxPoisonScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "inbox-poison";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues =>
        [new("inventory", PlaygroundAmqpNames.InventoryQueue(ScenarioSlug))];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exCmd = PlaygroundAmqpNames.CommandsExchange(ScenarioSlug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(ScenarioSlug);

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<ProcessOrderCommand>());

        bus.AddCommandConsumeChannel($"{ScenarioSlug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<ProcessOrderCommand>(m => m.WithHandler<ProcessOrderHandler>($"{ScenarioSlug}.process"))
            .UseInbox<ConsumerDbContext>());
    }

    public string Slug => ScenarioSlug;

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
        var mpCmd = new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(mpCmd, runId);
        db.OutboxMessages.Add(new ProcessOrderCommand(orderIdStr, runId), mpCmd);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
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

    [RatatoskrMessage("inbox-poison.process-order-command")]
    public sealed record ProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class ProcessOrderHandler(ILogger<ProcessOrderHandler> _) : IMessageHandler<ProcessOrderCommand>
    {
        public Task HandleAsync(ProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Simulated inventory inbox failure (poison scenario)."));
    }
}
