using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Composition;

internal static class PlaygroundRatatoskrRegistrations
{
    public static void Add_outbox_success(RatatoskrBuilder bus)
    {
        const string slug = "outbox-success";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessReserveStockInternalHandler>("outbox-success.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderFulfilledHandler>("outbox-success.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderFailedHandler>("outbox-success.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessProcessOrderHandler>("outbox-success.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderPlacedNotifyHandler>("outbox-success.notify")
                .WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderPlacedAnalyticsHandler>("outbox-success.analytics"))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxSuccess.OutboxSuccessOrderFulfilledNotifyHandler>("outbox-success.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_outbox_retry_then_success(RatatoskrBuilder bus)
    {
        const string slug = "outbox-retry-then-success";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessReserveStockInternalHandler>("outbox-retry-then-success.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderFulfilledHandler>("outbox-retry-then-success.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderFailedHandler>("outbox-retry-then-success.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessProcessOrderHandler>("outbox-retry-then-success.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderPlacedNotifyHandler>("outbox-retry-then-success.notify")
                .WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderPlacedAnalyticsHandler>("outbox-retry-then-success.analytics"))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess.OutboxRetryThenSuccessOrderFulfilledNotifyHandler>("outbox-retry-then-success.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_outbox_poison(RatatoskrBuilder bus)
    {
        const string slug = "outbox-poison";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonReserveStockInternalHandler>("outbox-poison.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderFulfilledHandler>("outbox-poison.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderFailedHandler>("outbox-poison.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonProcessOrderHandler>("outbox-poison.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderPlacedNotifyHandler>("outbox-poison.notify")
                .WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderPlacedAnalyticsHandler>("outbox-poison.analytics"))
            .Consumes<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Outbox.OutboxPoison.OutboxPoisonOrderFulfilledNotifyHandler>("outbox-poison.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_inbox_retry_then_success(RatatoskrBuilder bus)
    {
        const string slug = "inbox-retry-then-success";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessReserveStockInternalHandler>("inbox-retry-then-success.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderFulfilledHandler>("inbox-retry-then-success.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderFailedHandler>("inbox-retry-then-success.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessProcessOrderHandler>("inbox-retry-then-success.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderPlacedNotifyHandler>("inbox-retry-then-success.notify")
                .WithHandler<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderPlacedAnalyticsHandler>("inbox-retry-then-success.analytics"))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess.InboxRetryThenSuccessOrderFulfilledNotifyHandler>("inbox-retry-then-success.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_inbox_poison(RatatoskrBuilder bus)
    {
        const string slug = "inbox-poison";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonReserveStockInternalHandler>("inbox-poison.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderFulfilledHandler>("inbox-poison.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderFailedHandler>("inbox-poison.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonProcessOrderHandler>("inbox-poison.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderPlacedNotifyHandler>("inbox-poison.notify")
                .WithHandler<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderPlacedAnalyticsHandler>("inbox-poison.analytics"))
            .Consumes<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.InboxPoison.InboxPoisonOrderFulfilledNotifyHandler>("inbox-poison.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_business_rejection(RatatoskrBuilder bus)
    {
        const string slug = "business-rejection";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionReserveStockInternalHandler>("business-rejection.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderFulfilledHandler>("business-rejection.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderFailedHandler>("business-rejection.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionProcessOrderHandler>("business-rejection.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderPlacedNotifyHandler>("business-rejection.notify")
                .WithHandler<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderPlacedAnalyticsHandler>("business-rejection.analytics"))
            .Consumes<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Inbox.BusinessRejection.BusinessRejectionOrderFulfilledNotifyHandler>("business-rejection.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_direct_consume_success(RatatoskrBuilder bus)
    {
        const string slug = "direct-consume-success";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessReserveStockInternalHandler>("direct-consume-success.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderFulfilledHandler>("direct-consume-success.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderFailedHandler>("direct-consume-success.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessProcessOrderHandler>("direct-consume-success.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderPlacedNotifyHandler>("direct-consume-success.notify")
                .WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderPlacedAnalyticsHandler>("direct-consume-success.analytics"))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess.DirectConsumeSuccessOrderFulfilledNotifyHandler>("direct-consume-success.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_direct_consume_retry(RatatoskrBuilder bus)
    {
        const string slug = "direct-consume-retry";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryReserveStockInternalHandler>("direct-consume-retry.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderFulfilledHandler>("direct-consume-retry.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderFailedHandler>("direct-consume-retry.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryProcessOrderHandler>("direct-consume-retry.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderPlacedNotifyHandler>("direct-consume-retry.notify")
                .WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderPlacedAnalyticsHandler>("direct-consume-retry.analytics"))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry.DirectConsumeRetryOrderFulfilledNotifyHandler>("direct-consume-retry.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_direct_consume_dlq(RatatoskrBuilder bus)
    {
        const string slug = "direct-consume-dlq";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqReserveStockInternalHandler>("direct-consume-dlq.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderFulfilledHandler>("direct-consume-dlq.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderFailedHandler>("direct-consume-dlq.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqProcessOrderHandler>("direct-consume-dlq.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderPlacedNotifyHandler>("direct-consume-dlq.notify")
                .WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderPlacedAnalyticsHandler>("direct-consume-dlq.analytics"))
            .Consumes<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq.DirectConsumeDlqOrderFulfilledNotifyHandler>("direct-consume-dlq.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_fanout_two_handlers_on_orderplaced(RatatoskrBuilder bus)
    {
        const string slug = "fanout-two-handlers-on-orderplaced";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedReserveStockInternalHandler>("fanout-two-handlers-on-orderplaced.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderFulfilledHandler>("fanout-two-handlers-on-orderplaced.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderFailedHandler>("fanout-two-handlers-on-orderplaced.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedProcessOrderHandler>("fanout-two-handlers-on-orderplaced.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderPlacedNotifyHandler>("fanout-two-handlers-on-orderplaced.notify")
                .WithHandler<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderPlacedAnalyticsHandler>("fanout-two-handlers-on-orderplaced.analytics"))
            .Consumes<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced.FanoutTwoHandlersOnOrderplacedOrderFulfilledNotifyHandler>("fanout-two-handlers-on-orderplaced.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_efcore_internal_command(RatatoskrBuilder bus)
    {
        const string slug = "efcore-internal-command";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandReserveStockInternalHandler>("efcore-internal-command.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderFulfilledHandler>("efcore-internal-command.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderFailedHandler>("efcore-internal-command.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandProcessOrderHandler>("efcore-internal-command.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderPlacedNotifyHandler>("efcore-internal-command.notify")
                .WithHandler<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderPlacedAnalyticsHandler>("efcore-internal-command.analytics"))
            .Consumes<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.EfcoreInternalCommand.EfcoreInternalCommandOrderFulfilledNotifyHandler>("efcore-internal-command.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void Add_replay_dedups(RatatoskrBuilder bus)
    {
        const string slug = "replay-dedups";
        var exEvt = PlaygroundAmqpNames.EventsExchange(slug);
        var exCmd = PlaygroundAmqpNames.CommandsExchange(slug);
        var qOrders = PlaygroundAmqpNames.OrdersQueue(slug);
        var qInv = PlaygroundAmqpNames.InventoryQueue(slug);
        var qNot = PlaygroundAmqpNames.NotificationsQueue(slug);
        var internalCh = $"pg.{slug}.orders.internal";

        bus.AddCommandPublishChannel(internalCh, c => c
            .WithEfCore()
            .Produces<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsReserveStockInternal>());

        bus.AddCommandConsumeChannel(internalCh, c => c
            .Consumes<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsReserveStockInternal>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsReserveStockInternalHandler>("replay-dedups.reserve"))
            .UseInbox<PublisherDbContext>());

        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderPlaced>()
            .Produces<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderFulfilled>()
            .Produces<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderFailed>());

        bus.AddCommandPublishChannel(exCmd, c => c
            .WithRabbitMq(r => r.WithDirectExchange())
            .Produces<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsProcessOrderCommand>());

        bus.AddEventConsumeChannel($"{slug}-orders-inbox", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qOrders)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderFulfilledHandler>("replay-dedups.fulfilled"))
            .Consumes<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderFailed>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderFailedHandler>("replay-dedups.failed"))
            .UseInbox<PublisherDbContext>());

        bus.AddCommandConsumeChannel($"{slug}-inventory", c => c
            .WithRabbitMq(r => r
                .WithDirectExchange()
                .WithAmqpExchangeName(exCmd)
                .WithQueueName(qInv)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsProcessOrderCommand>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsProcessOrderHandler>("replay-dedups.process"))
            .UseInbox<ConsumerDbContext>());

        bus.AddEventConsumeChannel($"{slug}-notifications", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange()
                .WithAmqpExchangeName(exEvt)
                .WithQueueName(qNot)
                .WithQueueType(QueueType.Classic)
                .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
            .Consumes<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderPlaced>(m => m
                .WithHandler<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderPlacedNotifyHandler>("replay-dedups.notify")
                .WithHandler<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderPlacedAnalyticsHandler>("replay-dedups.analytics"))
            .Consumes<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderFulfilled>(m => m.WithHandler<PlaygroundHost.Scenarios.Other.ReplayDedups.ReplayDedupsOrderFulfilledNotifyHandler>("replay-dedups.fulfilled-notify"))
            .UseInbox<PublisherDbContext>());
    }

    public static void AddAllPipelineScenarios(RatatoskrBuilder bus)
    {
        Add_outbox_success(bus);
        Add_outbox_retry_then_success(bus);
        Add_outbox_poison(bus);
        Add_inbox_retry_then_success(bus);
        Add_inbox_poison(bus);
        Add_business_rejection(bus);
        Add_direct_consume_success(bus);
        Add_direct_consume_retry(bus);
        Add_direct_consume_dlq(bus);
        Add_fanout_two_handlers_on_orderplaced(bus);
        Add_efcore_internal_command(bus);
        Add_replay_dedups(bus);
    }
}