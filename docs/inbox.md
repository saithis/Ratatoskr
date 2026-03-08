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

### EF Core Transport Durability

The EF Core transport (`WithEfCore()`) writes messages directly to the inbox tables via `EfCoreMessageSender` → `InboxAcceptor`. There is no in-memory channel — all messages are persisted to the database before handler invocation.

When using the outbox with the same DbContext as the inbox, the `OutboxTriggerInterceptor` writes inbox entries in the **same database transaction** as business data. No outbox entry is created for same-DbContext channels — the inbox processor picks up the entries directly. For cross-DbContext scenarios, an outbox entry is created and delivered via the `OutboxProcessor`.

## Setup

### 1. Implement DbContext interfaces

DbContext classes must implement both `IInboxDbContext` and `IOutboxDbContext`:

```csharp
public class AppDbContext : DbContext, IOutboxDbContext, IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Pass Database to enable provider-specific partial indexes (PostgreSQL, SQL Server)
        modelBuilder.AddOutboxEntities(Database);
        modelBuilder.AddInboxEntities(Database);
    }
}
```

### 2. Configure durability with `AddEfCoreDurability`

Per-DbContext inbox and outbox options are configured centrally via `AddEfCoreDurability<TDbContext>()`. Channels then opt in to the inbox with `UseInbox<TDbContext>()` (no options).

```csharp
services.AddRatatoskr(bus =>
{
    // Configure durability for this DbContext — inbox + outbox options in one place
    bus.AddEfCoreDurability<AppDbContext>(d =>
    {
        d.UseInbox(inbox =>
        {
            inbox.WithMaxRetries(5);
            inbox.WithMaxRetryDelay(TimeSpan.FromMinutes(5));
            inbox.WithPollingInterval(TimeSpan.FromSeconds(30));
        });
        d.UseOutbox();
    });

    bus.AddEventPublishChannel("orders.events", c => c.WithEfCore().Produces<OrderPlaced>());
    bus.AddEventConsumeChannel("orders.events", c => c
        .Consumes<OrderPlaced>(m => m
            .WithHandler<FulfillmentHandler>("fulfillment")   // inbox-managed
            .WithHandler<NotificationHandler>("notification")) // inbox-managed
        .UseInbox<AppDbContext>());  // enables inbox on this channel
});

services.AddDbContext<AppDbContext>((sp, opts) =>
{
    opts.UseNpgsql("...");
    opts.RegisterOutbox<AppDbContext>(sp); // wire up outbox interceptor
});
```

### 3. Register handlers with stable keys

Handlers are registered inside the `Consumes<T>()` builder. Inbox-managed handlers provide a stable key; fire-and-forget handlers must explicitly opt out with `WithoutInbox()`:

```csharp
.Consumes<OrderPlaced>(m => m
    .WithHandler<FulfillmentHandler>("fulfillment")                    // inbox-managed
    .WithHandler<AuditLogHandler>("audit", h => h.WithoutInbox()))     // explicit fire-and-forget
```

The **handler key** (`"fulfillment"`) is persisted in the database. It must be stable across deployments — renaming the key will cause existing in-flight messages to be poisoned with an "unknown handler key" error.

> **Validation**: On channels with `UseInbox<TDbContext>()`, every handler must either provide a stable key (inbox-managed) or explicitly opt out with `WithoutInbox()`. Registering a handler without a key on an inbox channel throws `InvalidOperationException` at startup. Handler keys must be **globally unique** across all channels and DbContexts.

#### Handler registration API

| Method | Effect |
|---|---|
| `.WithHandler<THandler>("key")` | Register handler as inbox-managed with the given stable key. |
| `.WithHandler<THandler>("key", h => h.WithoutInbox())` | Register handler as fire-and-forget on an inbox channel (explicit opt-out). |
| `.WithHandler<THandler>()` | Register handler as fire-and-forget (only on channels **without** `UseInbox`). |

## Configuration Options

All options are configurable via fluent methods on the `AddEfCoreDurability` inbox builder:

```csharp
bus.AddEfCoreDurability<AppDbContext>(d => d.UseInbox(inbox =>
{
    inbox.WithMaxRetries(5);
    inbox.WithMaxRetryDelay(TimeSpan.FromMinutes(5));
    inbox.WithPollingInterval(TimeSpan.FromSeconds(30));
    inbox.WithBatchSize(100);
    inbox.WithStuckMessageThreshold(TimeSpan.FromMinutes(5));
    inbox.WithHandlerTimeout(TimeSpan.FromMinutes(2));
    inbox.WithRestartDelay(TimeSpan.FromSeconds(5));
    inbox.WithLockAcquireTimeout(TimeSpan.FromSeconds(60));
    inbox.WithLockName("custom-lock-name");
}));
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
| `WithLockName(string)` | `"InboxProcessor_{DbContextTypeName}"` | Distributed lock name. Auto-generated per DbContext type to avoid collisions. Override only if you need a custom name. |

## Outbox Configuration Options

The outbox builder supports the same set of tuning options as the inbox:

```csharp
bus.AddEfCoreDurability<AppDbContext>(d => d.UseOutbox(outbox =>
{
    outbox.WithMaxRetries(5);
    outbox.WithMaxRetryDelay(TimeSpan.FromMinutes(5));
    outbox.WithPollingInterval(TimeSpan.FromSeconds(60));
    outbox.WithBatchSize(100);
    outbox.WithStuckMessageThreshold(TimeSpan.FromMinutes(5));
    outbox.WithSendTimeout(TimeSpan.FromSeconds(30));
    outbox.WithRestartDelay(TimeSpan.FromSeconds(5));
    outbox.WithLockAcquireTimeout(TimeSpan.FromSeconds(60));
    outbox.WithLockName("custom-outbox-lock");
}));
```

| Fluent Method | Default | Description |
|---|---|---|
| `WithMaxRetries(int)` | `5` | Number of send attempts before marking a message as poisoned. A value of 0 means poisoned on the first failure. |
| `WithMaxRetryDelay(TimeSpan)` | `5 minutes` | Maximum backoff delay between retries. |
| `WithPollingInterval(TimeSpan)` | `60 seconds` | How often the background processor polls the DB when idle. |
| `WithBatchSize(int)` | `100` | Messages processed per batch. |
| `WithStuckMessageThreshold(TimeSpan)` | `5 minutes` | How long a message can remain in "processing" state before it is considered stuck. |
| `WithSendTimeout(TimeSpan)` | *none* | Maximum time a send operation is allowed to run. Timeout cancellation counts as a failure (increments ErrorCount). |
| `WithRestartDelay(TimeSpan)` | `5 seconds` | Delay before restarting the processor after an unexpected crash. |
| `WithLockAcquireTimeout(TimeSpan)` | `60 seconds` | Timeout for acquiring the distributed lock. |
| `WithLockName(string)` | `"OutboxProcessor_{DbContextTypeName}"` | Distributed lock name. Auto-generated per DbContext type. |

Use `WithoutBackgroundProcessing()` to disable the outbox background service in integration tests, analogous to the inbox pattern.

## Mixing Inbox and Non-Inbox Handlers

You can register both inbox-managed and fire-and-forget handlers for the same message type within the same `Consumes<T>()` builder. Fire-and-forget handlers on inbox channels must explicitly opt out:

```csharp
.Consumes<OrderPlaced>(m => m
    .WithHandler<FulfillmentHandler>("fulfillment")                    // inbox
    .WithHandler<AuditLogHandler>("audit", h => h.WithoutInbox()))     // explicit fire-and-forget
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

When using RabbitMQ, the `RabbitMqConsumer` delegates to `MessageRouter`, which calls `InboxAcceptor` before dispatching the message through `MessageDispatcher`. The consumer itself has no inbox awareness. The acceptor creates its own DI scope, so its `DbContext` is fully isolated from handler scopes. The message and handler statuses are persisted to the database, then the dispatcher invokes only fire-and-forget handlers. If all handlers are inbox-managed, the router treats this as success. `InboxProcessor` delivers inbox-managed handlers independently — failures in inbox-managed handlers do not affect broker acknowledgement.

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

`TrackedMessage.TransportName` reflects the transport that delivered the message to the inbox (e.g. `"rabbitmq"`, `"efcore"`).
`TrackedMessage.IsSuccess` indicates whether the handler succeeded (`true`) or failed (`false`) at the `InboxDispatched` stage.

## Testing

Use `WithoutBackgroundProcessing()` to disable the `InboxProcessor` background service in integration tests. This gives deterministic control over when inbox processing runs, so tests can inspect the database state between acceptance and delivery:

```csharp
services.AddRatatoskr(bus =>
{
    bus.AddEfCoreDurability<AppDbContext>(d =>
        d.UseInbox(inbox => inbox.WithoutBackgroundProcessing()));

    bus.AddEventPublishChannel("events", c => c.WithEfCore().Produces<OrderPlaced>());
    bus.AddEventConsumeChannel("events", c => c
        .Consumes<OrderPlaced>(m => m
            .WithHandler<FulfillmentHandler>("fulfillment"))
        .UseInbox<AppDbContext>());
});

// In the test body, trigger processing manually:
var processor = Services.GetRequiredService<InboxMessageProcessor<AppDbContext>>();
await processor.ProcessBatchAsync(includeStuckMessageDetection: false, CancellationToken.None);
```

## Multi-DbContext Support

Each consume channel can use a different `DbContext` for its inbox. This enables bounded context isolation — for example, an Orders service and a Shipping service sharing the same application but using separate databases.

Per-DbContext durability is configured centrally via `AddEfCoreDurability`, and channels opt in with `UseInbox`:

```csharp
services.AddRatatoskr(bus =>
{
    bus.AddEventPublishChannel("shared-events", c => c.WithEfCore().Produces<OrderPlaced>());

    // Per-DbContext durability configuration
    bus.AddEfCoreDurability<OrdersDbContext>(d => d.UseInbox());
    bus.AddEfCoreDurability<ShippingDbContext>(d => d.UseInbox());

    // Orders inbox — persists to OrdersDbContext
    bus.AddEventConsumeChannel("orders.inbox", c => c
        .Consumes<OrderPlaced>(m => m
            .WithHandler<FulfillmentHandler>("fulfillment"))
        .UseInbox<OrdersDbContext>());

    // Shipping inbox — persists to ShippingDbContext
    bus.AddEventConsumeChannel("shipping.inbox", c => c
        .Consumes<OrderPlaced>(m => m
            .WithHandler<ShipmentHandler>("shipment"))
        .UseInbox<ShippingDbContext>());
});

services.AddDbContext<OrdersDbContext>((sp, opts) =>
    opts.UseNpgsql("Host=...;Database=orders"));
services.AddDbContext<ShippingDbContext>((sp, opts) =>
    opts.UseNpgsql("Host=...;Database=shipping"));
```

Each `DbContext` type gets its own:
- `InboxProcessor<TDbContext>` background service
- `InboxMessageProcessor<TDbContext>` batch processor
- `InboxAcceptor<TDbContext>` for persistence
- Distributed lock (auto-named `InboxProcessor_{DbContextTypeName}`)

Per-DbContext services are registered once (idempotent), so multiple channels sharing the same `DbContext` reuse the same processor and options.

> **Important:** Each `DbContext` type is expected to have its own database. The `InboxMessageProcessor` queries all pending handler statuses from its database — if two `DbContext` types share a database, they will see each other's data.

### Opting out of inbox for specific handlers

On a channel with `UseInbox<TDbContext>()`, handlers with a stable key are automatically inbox-managed. To opt a specific handler out of inbox processing (making it fire-and-forget even on an inbox channel), use `WithoutInbox()`:

```csharp
.Consumes<OrderPlaced>(m => m
    .WithHandler<FulfillmentHandler>("fulfillment")          // inbox-managed
    .WithHandler<AuditLogHandler>("audit", h => h.WithoutInbox())) // fire-and-forget despite having a key
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
