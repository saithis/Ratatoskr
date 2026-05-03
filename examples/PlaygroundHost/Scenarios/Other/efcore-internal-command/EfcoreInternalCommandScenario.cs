using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;

namespace PlaygroundHost.Scenarios.Other.EfcoreInternalCommand;

public sealed class EfcoreInternalCommandScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "efcore-internal-command";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues => [];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var internalCh = $"pg.{ScenarioSlug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<ReserveStockInternal>()
            .Produces<OutboxSibling>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<ReserveStockInternal>(m => m.WithHandler<ReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .Consumes<OutboxSibling>(m => m.WithHandler<OutboxSiblingHandler>($"{ScenarioSlug}.sibling"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "EF Core internal command";

    public string Description =>
        "Two internal commands are staged in the same SaveChanges as the order; activity should show EF Core transport handling for ReserveStockInternal.";

    public string Topic => "Other";

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
        var mpRes = new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(order.Id) };
        var mpSib = new MessageProperties { Id = $"{order.Id:D}:efcore-sibling" };
        PlaygroundCorrelation.AttachToMessageProperties(mpRes, runId);
        PlaygroundCorrelation.AttachToMessageProperties(mpSib, runId);
        db.OutboxMessages.Add(new ReserveStockInternal(orderIdStr, runId), mpRes);
        db.OutboxMessages.Add(new OutboxSibling(orderIdStr, runId), mpSib);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var recorder = sp.GetRequiredService<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;
        await StageOrderAsync(db, time, runId, cancellationToken);
        context.StepsCompleted.Add("staged_with_internal_pair");

        await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
        var entries = recorder.GetEntriesForScenarioRun(runId);
        var hit = entries.Any(e =>
            (e.MessageType ?? "").Contains("reserve-stock-internal", StringComparison.OrdinalIgnoreCase) &&
            ((e.TransportName ?? "").Contains("EfCore", StringComparison.OrdinalIgnoreCase) ||
             e.Stage == nameof(MessageStage.InboxDispatched) ||
             e.Stage == nameof(MessageStage.Dispatched)));
        return hit
            ? new ScenarioVerdict(true)
            : new ScenarioVerdict(false, "No ReserveStockInternal / EF Core transport activity captured for this run yet.");
    }

    [RatatoskrMessage("efcore-internal-command.reserve-stock-internal")]
    public sealed record ReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("efcore-internal-command.outbox-sibling")]
    public sealed record OutboxSibling(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class ReserveStockInternalHandler(ILogger<ReserveStockInternalHandler> logger) : IMessageHandler<ReserveStockInternal>
    {
        public Task HandleAsync(ReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
        {
            logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
            return Task.CompletedTask;
        }
    }

    public sealed class OutboxSiblingHandler(ILogger<OutboxSiblingHandler> logger) : IMessageHandler<OutboxSibling>
    {
        public Task HandleAsync(OutboxSibling message, MessageProperties properties, CancellationToken cancellationToken)
        {
            logger.LogInformation("OutboxSibling processed for order {OrderId}", message.OrderId);
            return Task.CompletedTask;
        }
    }
}
