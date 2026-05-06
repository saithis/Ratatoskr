using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;

namespace PlaygroundHost.Scenarios.Other;

public sealed class EfcoreInternalCommandScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "efcore-internal-command";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues => [];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var internalCh = $"pg.{ScenarioSlug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<ReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<ReserveStockInternal>(m => m.WithHandler<ReserveStockInternalHandler>($"{ScenarioSlug}.reserve"))
            .UseInbox<PublisherDbContext>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "EF Core internal command";

    public string Description =>
        "Two ReserveStockInternal rows are staged in the same SaveChanges as the order; EF Core transport delivers both; activity should show handling for ReserveStockInternal.";

    public string Topic => "Other";

    private async Task StageOrderAsync(
        PublisherDbContext context,
        TimeProvider timeProvider,
        string runId,
        CancellationToken cancellationToken)
    {
        var order = this.AddPlacedOrderToContext(context, timeProvider, "outbox");
        this.StageCorrelatedOutboxMessage(
            context,
            runId,
            new ReserveStockInternal(order.Id.ToString(), runId),
            PlaygroundMessageIds.ReserveStockInternal(order.Id));
        this.StageCorrelatedOutboxMessage(
            context,
            runId,
            new ReserveStockInternal(order.Id.ToString(), runId),
            $"{order.Id:D}:efcore-reserve-second");
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var recorder = context.GetRequired<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;
        await StageOrderAsync(context.PublisherDb, context.TimeProvider, runId, cancellationToken);
        context.StepsCompleted.Add("staged_two_internal_same_save");

        await Task.Delay(ScenarioTiming.EfCoreActivitySettleDelay, cancellationToken);
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

    public sealed class ReserveStockInternalHandler(ILogger<ReserveStockInternalHandler> logger) : IMessageHandler<ReserveStockInternal>
    {
        public Task HandleAsync(ReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
        {
            logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
            return Task.CompletedTask;
        }
    }
}
