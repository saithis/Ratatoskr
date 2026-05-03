using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess;

public sealed class DirectConsumeSuccessScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "direct-consume-success";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues =>
        [new("work", PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug))];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        var qWork = PlaygroundAmqpNames.NotificationsQueue(ScenarioSlug);

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<DirectWork>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-work", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qWork)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<DirectWork>(m => m.WithHandler<DirectWorkHandler>()));
    }

    public string Slug => ScenarioSlug;

    public string Title => "Direct publish happy path";

    public string Description =>
        "PublishDirectAsync to one topic exchange and one consumer (no inbox); handler marks the order Fulfilled.";

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
        var mp = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(mp, runId);
        await bus.PublishDirectAsync(new DirectWork(orderIdStr, runId), mp, cancellationToken);
        context.StepsCompleted.Add("direct_publish_one_message");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            order.Id,
            OrderStatus.Fulfilled,
            TimeSpan.FromSeconds(90),
            time,
            cancellationToken);
    }

    [RatatoskrMessage("direct-consume-success.direct-work")]
    public sealed record DirectWork(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    public sealed class DirectWorkHandler(
        PublisherDbContext db,
        TimeProvider time,
        ILogger<DirectWorkHandler> logger) : IMessageHandler<DirectWork>
    {
        public async Task HandleAsync(DirectWork message, MessageProperties properties, CancellationToken cancellationToken)
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
            if (order is null) return;
            var now = time.GetUtcNow().UtcDateTime;
            order.Status = OrderStatus.Fulfilled;
            order.StatusChangedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Order {OrderId} marked Fulfilled", message.OrderId);
        }
    }
}
