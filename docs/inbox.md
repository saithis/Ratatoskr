# Inbox Pattern (`Ratatoskr.EfCore`)

The inbox pattern provides **durable, per-handler delivery** of received messages. Once a message is accepted into the inbox (persisted to the database), each registered handler will process it exactly once — even if the application crashes mid-delivery — with automatic exponential-backoff retry and per-handler deduplication.

## When to Use

Use the inbox pattern when:

- A handler failure must not prevent other handlers from succeeding (per-handler isolation).
- Message processing must survive application crashes without redelivery from the broker.
- Exactly-once delivery semantics are required for a given (message ID, handler) pair.
- You want durable retry with backoff instead of relying entirely on the transport's retry mechanism.

## How It Works

### Two-table schema

| Table | Purpose |
|---|---|
| `InboxMessages` | One row per unique CloudEvents `id` received. Stores the serialized message body and properties. |
| `InboxHandlerStatuses` | One row per (message, handler) pair. Tracks retry state, backoff schedule, and completion. |

A unique constraint on `(MessageId, HandlerKey)` is the **deduplication key**: the same handler will never run twice for the same message ID, even on redelivery.

### Processing flow

1. A message arrives (from RabbitMQ, the outbox, or a direct publish).
2. The message and one `InboxHandlerStatus` row per inbox-managed handler are written to the database **before** the transport acknowledges receipt.
3. The background `InboxProcessor` polls (and is also woken up via a trigger channel) for pending handler statuses and delivers them in batches.
4. On success: `CompletedAt` is set on the status row.
5. On failure: `ErrorCount` is incremented, `NextAttemptAt` is set using exponential backoff (`2^n` seconds, capped at `MaxRetryDelay`).
6. After `MaxRetries` failures: the status is marked `IsPoisoned = true` and no longer retried (kept for future manual retry).

### Crash safety (local transport)

When using `UseEfCoreInbox` together with `UseLocalTransport`, the regular `LocalMessageSender` is replaced by `DurableLocalMessageSender`. Its `SendAsync` writes inbox entries to the database **before** writing to the in-memory channel. Combined with the outbox's deduplication, this guarantees no message loss at any crash point in the pipeline.

## Setup

### 1. Implement `IInboxDbContext`

```csharp
public class AppDbContext : DbContext, IOutboxDbContext, IInboxDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddOutboxEntities(); // if using outbox
        modelBuilder.AddInboxEntities();
    }
}
```

### 2. Register with `AddRatatoskr`

`UseEfCoreInbox` can be called in any order relative to `UseLocalTransport` or `UseRabbitMq`.

```csharp
services.AddRatatoskr(bus =>
{
    bus.UseLocalTransport(); // or UseRabbitMq(...)
    bus.UseEfCoreInbox<AppDbContext>(inbox =>
    {
        inbox.WithMaxRetries(5);
        inbox.WithMaxRetryDelay(TimeSpan.FromMinutes(5));
        inbox.WithPollingInterval(TimeSpan.FromSeconds(30));
    });

    // Inbox-managed handlers — opted in via WithInbox("stable-key")
    bus.AddHandler<OrderPlaced, FulfillmentHandler>(cfg => cfg.WithInbox("fulfillment"));
    bus.AddHandler<OrderPlaced, NotificationHandler>(cfg => cfg.WithInbox("notification"));

    // Fire-and-forget (non-inbox) handler — existing behaviour
    bus.AddHandler<OrderPlaced, AuditLogHandler>();
});

services.AddDbContext<AppDbContext>((sp, opts) =>
{
    opts.UseNpgsql("...");
});
```

### 3. Register handlers with stable keys

```csharp
// Inbox-managed: durable delivery, per-handler retry, deduplication
bus.AddHandler<OrderPlaced, FulfillmentHandler>(cfg => cfg.WithInbox("fulfillment"));

// Fire-and-forget: synchronous, no deduplication
bus.AddHandler<OrderPlaced, AuditLogHandler>();
```

The **handler key** (`"fulfillment"`) is persisted in the database. It must be stable across deployments — renaming the key will cause existing in-flight messages to be poisoned with an "unknown handler key" error.

#### Per-handler inbox opt-in API

| Method | Effect |
|---|---|
| `cfg.WithInbox("key")` | Enroll handler in inbox with the given stable key. |
| `cfg.WithInbox()` | Enroll with the handler's CLR full name as the stable key. |
| `cfg.WithoutInbox()` | Explicitly exclude this handler from the inbox (overrides global default). |
| *(no call)* | Determined by `WithDefaultInboxEnabled()` — excluded by default. |

#### Global default opt-in

Use `WithDefaultInboxEnabled()` to automatically enroll all handlers that have not explicitly called `WithoutInbox()`:

```csharp
bus.UseEfCoreInbox<AppDbContext>(inbox => inbox.WithDefaultInboxEnabled());
// All AddHandler calls without WithoutInbox() are automatically enrolled.
// The handler's CLR full name is used as the stable key.
```

## Configuration Options

| Option | Default | Description |
|---|---|---|
| `MaxRetries` | `5` | Number of delivery attempts before marking a status as poisoned. |
| `MaxRetryDelay` | `5 minutes` | Maximum backoff delay (`2^n` seconds, capped). |
| `PollingInterval` | `30 seconds` | How often the background processor polls the DB when idle. |
| `BatchSize` | `100` | Handler statuses processed per batch. |
| `StuckMessageThreshold` | `5 minutes` | How long a status can remain in "processing" state before it is considered stuck (crash recovery). |
| `LockName` | `"InboxProcessor"` | Distributed lock name. Change if you run multiple inboxes or conflict with the outbox lock. |

## Mixing Inbox and Non-Inbox Handlers

You can register both inbox-managed and fire-and-forget handlers for the same message type:

```csharp
bus.AddHandler<OrderPlaced, FulfillmentHandler>(cfg => cfg.WithInbox("fulfillment")); // inbox
bus.AddHandler<OrderPlaced, AuditLogHandler>();                                        // fire-and-forget
```

- **Non-inbox handlers** are called synchronously during message dispatch (existing behaviour).
- **Inbox-managed handlers** are queued to the database and delivered by `InboxProcessor`.

> **Recommendation**: avoid mixing on the same consume channel where possible. If a non-inbox handler fails, the transport may redeliver the message; inbox handlers will deduplicate correctly, but non-inbox handlers will run again.

## Deduplication

Deduplication is per **(message ID, handler key)**. If the same CloudEvents `id` is received twice (e.g. RabbitMQ redelivery or outbox retry), the second delivery is a no-op for the inbox: the unique constraint on `(MessageId, HandlerKey)` prevents duplicate handler status rows from being inserted.

## RabbitMQ Integration

When using RabbitMQ, the `InboxInterceptor` is called by `MessageDispatcher` before handler dispatch. The message and handler statuses are persisted to the database, the broker message is acknowledged, and `InboxProcessor` delivers to each handler independently. Handler failures no longer affect broker acknowledgement.

## Observability

Two new `MessageStage` values are emitted to all registered `IMessageActivityObserver` implementations:

| Stage | When |
|---|---|
| `MessageStage.InboxQueued` | Message accepted into inbox (interceptor called). |
| `MessageStage.InboxDispatched` | A single handler status was attempted by `InboxProcessor` (success or failure). |

When using `Ratatoskr.Testing`, these stages are available on `MessageTrackingSession`:

```csharp
await using var session = Services.CreateTrackingSession();
// ... publish message ...
var queued = await session.WaitForInboxQueued<MyMessage>();
var dispatched = await session.WaitForInboxDispatched<MyMessage>();
```

`TrackedMessage.TransportName` reflects the transport that delivered the message to the inbox (e.g. `"rabbitmq"`, `"local"`).

## Testing

Use `WithoutBackgroundProcessing()` to disable the `InboxProcessor` background service in integration tests. This gives deterministic control over when inbox processing runs, so tests can inspect the database state between acceptance and delivery:

```csharp
services.AddRatatoskr(bus =>
{
    bus.UseLocalTransport();
    bus.AddHandler<OrderPlaced, FulfillmentHandler>(cfg => cfg.WithInbox("fulfillment"));
    bus.UseEfCoreInbox<AppDbContext>(inbox => inbox.WithoutBackgroundProcessing());
});

// In the test body, trigger processing manually:
var processor = Services.GetRequiredService<InboxMessageProcessor<AppDbContext>>();
await processor.ProcessBatchAsync(includeStuckMessageDetection: false, CancellationToken.None);
```
