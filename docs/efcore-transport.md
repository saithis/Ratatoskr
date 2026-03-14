# EF Core Transport

The EF Core transport delivers messages via the database instead of a message broker. Messages are written directly to inbox tables, making it ideal for single-service applications, development environments, or scenarios where running a broker is not practical.

> [!IMPORTANT]
> The EF Core transport is a **delivery mechanism** — it moves messages from publisher to consumer via the database. It is separate from EF Core **durability** (outbox/inbox patterns). You can use the EF Core transport without an outbox, and you can use the outbox with RabbitMQ instead of the EF Core transport. These are independent choices.

## When to Use

| Use EF Core Transport When | Use RabbitMQ When |
|----------------------------|-------------------|
| Single service, no broker needed | Multiple services communicating |
| Development/testing environment | Production with high throughput |
| Messages stay within one application | Cross-service messaging required |
| Simplest possible setup | Advanced routing (topic, fanout) needed |

## Setup

The EF Core transport requires inbox configuration because messages are delivered by writing directly to inbox tables:

```csharp
builder.Services.AddRatatoskr(bus =>
{
    bus.AddEfCoreDurability<OrderDbContext>(d =>
    {
        d.UseOutbox();
        d.UseInbox();
    });

    // Publish channel with EF Core transport
    bus.AddEventPublishChannel("orders.events", c => c
        .WithEfCore()
        .Produces<OrderPlaced>());

    // Consume channel with inbox
    bus.AddEventConsumeChannel("orders.events", c => c
        .Consumes<OrderPlaced>(m => m
            .WithHandler<OrderPlacedHandler>("order-handler"))
        .UseInbox<OrderDbContext>());
});
```

The DbContext must implement both `IOutboxDbContext` and `IInboxDbContext`:

[!code-csharp[](../examples/Docs/Data/OrderDbContext.cs#OrderDbContext)]

## How It Works

The EF Core transport has no in-memory channel, no consumer loop, and no polling for new messages from a broker. The flow is:

1. `EfCoreMessageSender.SendAsync()` writes the message directly to the inbox tables via `InboxAcceptor`
2. The `InboxProcessor` background service picks up pending handler statuses
3. Each handler is invoked with retry and deduplication

### Same-DbContext Optimization

When the outbox and inbox use the same `DbContext`, Ratatoskr optimizes by writing inbox entries in the **same database transaction** as your business data. No outbox row is created — the inbox processor picks up the entries directly.

```csharp
// Business data + message in one transaction
db.OutboxMessages.Add(new OrderPlaced(orderId, email, total));
await db.SaveChangesAsync();
// → Inbox entries created in the same transaction
// → InboxProcessor delivers to handlers
```

### Cross-DbContext Delivery

When the publish channel and consume channel use different `DbContext` types, the outbox processor sends the message through `EfCoreMessageSender` to the target inbox.

## Requirements

All consume channels using the EF Core transport **must** have `UseInbox<TDbContext>()` configured, and all handlers must have stable keys. A channel without `UseInbox` will cause an `InvalidOperationException` at send time.

This means the fire-and-forget handler pattern is not available with the EF Core transport. To mix durable and fire-and-forget delivery, use RabbitMQ for the fire-and-forget channel or use a separate message type.

## Comparison with RabbitMQ

| Feature | EF Core Transport | RabbitMQ Transport |
|---------|-------------------|-------------------|
| Infrastructure | Database only | RabbitMQ broker |
| Delivery | Via inbox tables | Via AMQP queues |
| Routing | By channel name | Exchange + routing key |
| Retry | Inbox retry (exponential backoff) | Retry queue + DLQ |
| Multi-service | Same database required | Network-based |
| Throughput | Limited by database | Higher (broker-optimized) |
| Handler keys | Required (always inbox-managed) | Optional (fire-and-forget available) |

## What's Next

- [RabbitMQ](rabbitmq.md) — Production transport with advanced routing
- [Outbox](outbox.md) — Transactional publishing with the EF Core transport
- [Inbox](inbox.md) — Handler delivery, retry, and deduplication
