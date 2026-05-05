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

namespace PlaygroundHost.Scenarios.Inbox.BusinessRejection;

public sealed class BusinessRejectionScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "business-rejection";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues =>
    [
        new("orders", PlaygroundAmqpNames.OrdersQueue(ScenarioSlug)),
        new("inventory", PlaygroundAmqpNames.InventoryQueue(ScenarioSlug)),
    ];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(ScenarioSlug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(ScenarioSlug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(ScenarioSlug);

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<OrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<ProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{ScenarioSlug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<OrderFailed>(m => m.WithHandler<OrderFailedHandler>($"{ScenarioSlug}.failed"))
            .UseInbox<PublisherDbContext>());

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

    public string Title => "Business rejection (OrderFailed)";

    public string Description =>
        "Inventory reject mode stages OrderFailed; publisher marks the order Failed.";

    public string Topic => "Inbox";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var runId = context.ScenarioRunId;
        var orderId = await this.StageOrderWithCommandAsync(
            context.PublisherDb, context.TimeProvider, runId,
            (id, rid) => new ProcessOrderCommand(id, rid),
            cancellationToken);
        context.StepsCompleted.Add("inventory_reject_mode");
        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Failed,
            ScenarioTiming.OrderEventuallyLong,
            context.TimeProvider,
            cancellationToken);
    }

    [RatatoskrMessage("business-rejection.process-order-command")]
    public sealed record ProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

    [RatatoskrMessage("business-rejection.order-failed")]
    public sealed record OrderFailed(string OrderId, string ScenarioRunId, string Reason) : IPlaygroundCorrelatedOrderMessage;

    public sealed class ProcessOrderHandler(ConsumerDbContext context, ILogger<ProcessOrderHandler> _) : IMessageHandler<ProcessOrderCommand>
    {
        public async Task HandleAsync(ProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
        {
            var orderGuid = Guid.Parse(message.OrderId);
            context.OutboxMessages.Add(
                new OrderFailed(
                    message.OrderId,
                    message.ScenarioRunId,
                    "Simulated business rejection."),
                new MessageProperties { Id = PlaygroundMessageIds.OrderFailed(orderGuid) });
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public sealed class OrderFailedHandler(PublisherDbContext context, TimeProvider timeProvider, ILogger<OrderFailedHandler> _)
        : IMessageHandler<OrderFailed>
    {
        public Task HandleAsync(OrderFailed message, MessageProperties _, CancellationToken cancellationToken) =>
            IScenarioExtensions.UpdateOrderStatusAsync(null!, context, timeProvider, message.OrderId, OrderStatus.Failed, cancellationToken);
    }
}
