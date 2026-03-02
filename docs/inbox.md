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
| `InboxHandlerStatuses` | One row per (message, handler) pair. Tracks retry state, backoff schedule, completion, and `CreatedAt` timestamp. |

A unique constraint on `(MessageId, HandlerKey)` is the **deduplication key**: the same handler will never run twice for the same message ID, even on redelivery.

### Processing flow

1. A message arrives (from RabbitMQ, the outbox, or a direct publish).
2. The message and one `InboxHandlerStatus` row per inbox-managed handler are written to the database **before** the transport acknowledges receipt.
3. The background `InboxProcessor` polls (and is also woken up via a trigger channel) for pending handler statuses and delivers them in batches.
4. On success: `CompletedAt` is set on the status row. Each handler's result is **persisted immediately** — completed handlers are not lost if a subsequent handler or the process fails.
5. On failure: `ErrorCount` is incremented, `NextAttemptAt` is set using exponential backoff (`2^n` seconds, capped at `MaxRetryDelay`).
6. After `MaxRetries` failures: the status is marked `IsPoisoned = true` and no longer retried (kept for future manual retry).
7. If the application shuts down (cancellation) while a handler is running, the attempt is **not** counted as a failure. The handler status remains in "processing" state and is recovered by stuck message detection on the next startup.
8. For deterministically unrecoverable errors (e.g. `InboxMessage` row deleted, handler key unregistered), the status is poisoned **immediately** without going through the retry cycle.

### Crash safety (local transport)

When using `UseEfCoreInbox` together with `UseLocalTransport`, the regular `LocalMessageSender` is replaced by `DurableLocalMessageSender`. Its `SendAsync` writes inbox entries to the database **before** writing to the in-memory channel. Combined with the outbox's deduplication, this guarantees no message loss at any crash point in the pipeline.

> **Important:** This replacement means that local publish calls (`PublishDirectAsync`) now depend on database availability and will have database-level latency. If the database is slow or unavailable, local publishes will be affected. Plan database capacity accordingly.

## Setup

### 1. Implement `IInboxDbContext`

```csharp
public class AppDbContext : DbContext, IOutboxDbContext, IInboxDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Pass Database to enable provider-specific partial indexes (PostgreSQL, SQL Server)
        modelBuilder.AddOutboxEntities(Database); // if using outbox
        modelBuilder.AddInboxEntities(Database);
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
    bus.AddHandler<OrderPlaced, FulfillmentHandler>(h => h.WithInbox("fulfillment"));
    bus.AddHandler<OrderPlaced, NotificationHandler>(h => h.WithInbox("notification"));

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
bus.AddHandler<OrderPlaced, FulfillmentHandler>(h => h.WithInbox("fulfillment"));

// Fire-and-forget: synchronous, no deduplication
bus.AddHandler<OrderPlaced, AuditLogHandler>();
```

The **handler key** (`"fulfillment"`) is persisted in the database. It must be stable across deployments — renaming the key will cause existing in-flight messages to be poisoned with an "unknown handler key" error.

> **Validation**: Each handler key must be unique. Registering two handlers with the same key throws `InvalidOperationException` at startup.

#### Per-handler inbox opt-in API

| Method | Effect |
|---|---|
| `h.WithInbox("key")` | Enroll handler in inbox with the given stable key. |
| `h.WithInbox()` | Enroll with the handler's CLR full name as the stable key. |
| `h.WithoutInbox()` | Explicitly exclude this handler from the inbox (overrides global default). |
| *(no call)* | Determined by `WithDefaultInboxEnabled()` — excluded by default. |

#### Global default opt-in

Use `WithDefaultInboxEnabled()` to automatically enroll all handlers that have not explicitly called `WithoutInbox()`:

```csharp
bus.UseEfCoreInbox<AppDbContext>(inbox => inbox.WithDefaultInboxEnabled());
// All AddHandler calls without WithoutInbox() are automatically enrolled.
// The handler's CLR full name is used as the stable key.
```

## Configuration Options

All options are configurable via fluent methods on `InboxBuilder<TDbContext>`:

```csharp
bus.UseEfCoreInbox<AppDbContext>(inbox =>
{
    inbox.WithMaxRetries(5);
    inbox.WithMaxRetryDelay(TimeSpan.FromMinutes(5));
    inbox.WithPollingInterval(TimeSpan.FromSeconds(30));
    inbox.WithBatchSize(100);
    inbox.WithStuckMessageThreshold(TimeSpan.FromMinutes(5));
    inbox.WithHandlerTimeout(TimeSpan.FromMinutes(2));
    inbox.WithRestartDelay(TimeSpan.FromSeconds(5));
    inbox.WithLockAcquireTimeout(TimeSpan.FromSeconds(60));
    inbox.WithLockName("InboxProcessor");
});
```

| Fluent Method | Default | Description |
|---|---|---|
| `WithMaxRetries(int)` | `5` | Number of delivery attempts before marking a status as poisoned. Must be >= 1. A value of 1 means one attempt, poisoned on the first failure. |
| `WithMaxRetryDelay(TimeSpan)` | `5 minutes` | Maximum backoff delay (`2^n` seconds, capped). Jitter is applied to prevent thundering herd. |
| `WithPollingInterval(TimeSpan)` | `30 seconds` | How often the background processor polls the DB when idle. |
| `WithBatchSize(int)` | `100` | Handler statuses processed per batch. |
| `WithStuckMessageThreshold(TimeSpan)` | `5 minutes` | How long a status can remain in "processing" state before it is considered stuck (crash recovery). |
| `WithHandlerTimeout(TimeSpan)` | *none* | Maximum time a handler is allowed to run. Timeout cancellation counts as a failure (increments ErrorCount). |
| `WithRestartDelay(TimeSpan)` | `5 seconds` | Delay before restarting the processor after an unexpected crash. |
| `WithLockAcquireTimeout(TimeSpan)` | `60 seconds` | Timeout for acquiring the distributed lock. |
| `WithLockName(string)` | `"InboxProcessor"` | Distributed lock name. Change if you run multiple inboxes or conflict with the outbox lock. |

## Mixing Inbox and Non-Inbox Handlers

You can register both inbox-managed and fire-and-forget handlers for the same message type:

```csharp
bus.AddHandler<OrderPlaced, FulfillmentHandler>(h => h.WithInbox("fulfillment")); // inbox
bus.AddHandler<OrderPlaced, AuditLogHandler>();                                        // fire-and-forget
```

- **Non-inbox handlers** are called synchronously during message dispatch, each in its own DI scope.
- **Inbox-managed handlers** are queued to the database and delivered by `InboxProcessor`.
- Each handler and the inbox acceptor run in **separate DI scopes**, so a failure or `ChangeTracker.Clear()` in one scope cannot affect another.

> **Recommendation**: avoid mixing on the same consume channel where possible. If a non-inbox handler fails, the transport may redeliver the message; inbox handlers will deduplicate correctly, but non-inbox handlers will run again.

## Deduplication

Deduplication is per **(message ID, handler key)**. If the same CloudEvents `id` is received twice (e.g. RabbitMQ redelivery or outbox retry), the second delivery is a no-op for the inbox: the unique constraint on `(MessageId, HandlerKey)` prevents duplicate handler status rows from being inserted.

> **Note**: The CloudEvents `id` (i.e., `MessageProperties.Id`) must not exceed **200 characters**. Messages with IDs longer than this limit are rejected with an `InvalidOperationException` before the database insert is attempted.

## Distributed Lock Safety

`InboxProcessor` acquires a distributed lock before processing batches to prevent multiple instances from processing the same messages concurrently. If the lock is lost mid-processing (e.g. network partition, Postgres connection drop), the processor detects it immediately via `HandleLostToken` and stops processing. Any in-flight handler status that was being processed will be picked up by stuck message detection on the next cycle.

## RabbitMQ Integration

When using RabbitMQ, the `RabbitMqConsumer` delegates to `MessageRouter`, which calls `InboxAcceptor` before dispatching the message through `MessageDispatcher`. The consumer itself has no inbox awareness. The acceptor creates its own DI scope, so its `DbContext` is fully isolated from handler scopes. The message and handler statuses are persisted to the database, then the dispatcher invokes only non-inbox handlers. If all handlers are inbox-managed, the router treats this as success. `InboxProcessor` delivers inbox-managed handlers independently — handler failures no longer affect broker acknowledgement.

## Observability

Three `MessageStage` values are emitted to all registered `IMessageActivityObserver` implementations:

| Stage | When |
|---|---|
| `MessageStage.InboxQueued` | Message accepted into inbox (interceptor called). |
| `MessageStage.InboxDispatched` | A single handler status was attempted by `InboxProcessor` (success or failure). `IsSuccess` is `true` on success, `false` on failure. `Exception` is set on failure. |
| `MessageStage.InboxPoisoned` | A handler status exceeded `MaxRetries` and was marked as poisoned. |

The `ratatoskr.inbox.poison.count` metric counter is incremented each time a handler status is poisoned.

When using `Ratatoskr.Testing`, these stages are available on `MessageTrackingSession`:

```csharp
await using var session = Services.CreateTrackingSession();
// ... publish message ...
var queued = await session.WaitForInboxQueued<MyMessage>();
var dispatched = await session.WaitForInboxDispatched<MyMessage>();
var poisoned = await session.WaitForInboxPoisoned<MyMessage>();
```

`TrackedMessage.TransportName` reflects the transport that delivered the message to the inbox (e.g. `"rabbitmq"`, `"local"`).
`TrackedMessage.IsSuccess` indicates whether the handler succeeded (`true`) or failed (`false`) at the `InboxDispatched` stage.

## Testing

Use `WithoutBackgroundProcessing()` to disable the `InboxProcessor` background service in integration tests. This gives deterministic control over when inbox processing runs, so tests can inspect the database state between acceptance and delivery:

```csharp
services.AddRatatoskr(bus =>
{
    bus.UseLocalTransport();
    bus.AddHandler<OrderPlaced, FulfillmentHandler>(h => h.WithInbox("fulfillment"));
    bus.UseEfCoreInbox<AppDbContext>(inbox => inbox.WithoutBackgroundProcessing());
});

// In the test body, trigger processing manually:
var processor = Services.GetRequiredService<InboxMessageProcessor<AppDbContext>>();
await processor.ProcessBatchAsync(includeStuckMessageDetection: false, CancellationToken.None);
```

## Data Retention

The `InboxMessages` and `InboxHandlerStatuses` tables grow unbounded as messages are processed. Completed and poisoned rows are never deleted automatically — you should implement periodic cleanup based on your retention requirements.

Example SQL for PostgreSQL:

```sql
-- Delete handler statuses completed more than 30 days ago
DELETE FROM "InboxHandlerStatuses"
WHERE "CompletedAt" IS NOT NULL
  AND "CompletedAt" < NOW() - INTERVAL '30 days';

-- Delete poisoned statuses older than 30 days
DELETE FROM "InboxHandlerStatuses"
WHERE "IsPoisoned" = true
  AND "CreatedAt" < NOW() - INTERVAL '30 days';

-- Delete orphaned inbox messages (no remaining handler statuses)
DELETE FROM "InboxMessages"
WHERE NOT EXISTS (
    SELECT 1 FROM "InboxHandlerStatuses"
    WHERE "MessageId" = "InboxMessages"."Id"
);
```
