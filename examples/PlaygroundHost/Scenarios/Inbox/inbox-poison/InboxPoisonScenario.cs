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
        var order = PlaygroundScenarioStaging.AddPlacedOrderToContext(db, time, "outbox");
        var orderIdStr = order.Id.ToString();
        PlaygroundScenarioStaging.StageCorrelatedOutboxMessage(
            db,
            runId,
            new ProcessOrderCommand(orderIdStr, runId),
            PlaygroundMessageIds.ProcessOrderCommand(order.Id));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var time = context.GetTimeProvider();
        var pub = context.GetPublisherDb();
        var runId = context.ScenarioRunId;
        int before;
        await using (var conScope = context.ScopeFactory.CreateAsyncScope())
        {
            var conDb = conScope.ServiceProvider.GetRequiredService<ConsumerDbContext>();
            before = await PlaygroundSqlMetrics.CountPoisonedInboxForScenarioRunAsync(conDb, runId, cancellationToken);
        }

        await StageOrderAsync(pub, time, runId, cancellationToken);
        context.StepsCompleted.Add("inventory_throw_mode");

        return await ScenarioAssertions.IntMetricEventuallyExceedsBaselineAsync(
            time,
            ScenarioTiming.PollLoopLong,
            ScenarioTiming.PollIntervalSlow,
            before,
            async ct =>
            {
                await using var scope2 = context.ScopeFactory.CreateAsyncScope();
                var db2 = scope2.ServiceProvider.GetRequiredService<ConsumerDbContext>();
                return await PlaygroundSqlMetrics.CountPoisonedInboxForScenarioRunAsync(db2, runId, ct);
            },
            "Poisoned consumer inbox count",
            cancellationToken);
    }

    [RatatoskrMessage("inbox-poison.process-order-command")]
    public sealed record ProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class ProcessOrderHandler(ILogger<ProcessOrderHandler> _) : IMessageHandler<ProcessOrderCommand>
    {
        public Task HandleAsync(ProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Simulated inventory inbox failure (poison scenario)."));
    }
}
