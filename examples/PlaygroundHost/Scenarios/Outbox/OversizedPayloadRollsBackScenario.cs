using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using Ratatoskr;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.Outbox;

public sealed class OversizedPayloadRollsBackScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "oversized-payload-rolls-back";
    private static string ExchangeName { get; } =
        PlaygroundAmqpNames.ExchangeName(ScenarioSlug, "events");

    public static IReadOnlyList<PlaygroundRabbitQueue> RabbitQueues => [];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        bus.AddEventPublishChannel(
            ExchangeName,
            c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<OrderPlaced>()
        );
    }

    public string Slug => ScenarioSlug;

    public string Title => "Oversized outbox payload";

    public string Description =>
        "Stages an OrderPlaced payload larger than the configured outbox max size; SaveChanges must roll back so the order row is not persisted.";

    public string Topic => "Outbox";

    public async Task<ScenarioVerdict> ExecuteAsync(
        ScenarioExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var order = this.AddPlacedOrderToContext(
            context.PublisherDb,
            context.TimeProvider,
            "outbox"
        );

        context.PublisherDb.OutboxMessages.Add(
            new OrderPlaced(order.Id.ToString("D"), context.ScenarioRunId, new string('x', 50_000)),
            this.CreateMessageProperties(context, PlaygroundMessageIds.OrderPlaced(order.Id))
        );
        try
        {
            await context.PublisherDb.SaveChangesAsync(cancellationToken);
            return new ScenarioVerdict(
                false,
                "Expected SaveChanges to fail for oversized payload."
            );
        }
        catch
        {
            await using var scope2 = context.ScopeFactory.CreateAsyncScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<PublisherDbContext>();
            var orderRowExists = await db2
                .Orders.AsNoTracking()
                .AnyAsync(o => o.Id == order.Id, cancellationToken);
            return orderRowExists
                ? new ScenarioVerdict(
                    false,
                    "Order row exists after failed save; expected rollback."
                )
                : new ScenarioVerdict(true);
        }
    }

    [RatatoskrMessage("oversized-payload-rolls-back.order-placed")]
    public sealed record OrderPlaced(
        string OrderId,
        string ScenarioRunId,
        string? BulkPaddingForDemo
    ) : IPlaygroundCorrelatedOrderMessage;
}
