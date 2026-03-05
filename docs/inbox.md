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
| `InboxMessages` | One row per unique CloudEvents `id` received. Stores the serialized message body, properties, and consume channel name. |
| `InboxHandlerStatuses` | One row per (message, handler) pair. Tracks retry state, backoff schedule, completion, and `CreatedAt` timestamp. |

A unique constraint on `(MessageId, HandlerKey)` is the **deduplication key**: the same handler will never run twice for the same message ID, even on redelivery.

### Processing flow

1. A message arrives (from RabbitMQ, the outbox, or a direct publish).
2. The `CompositeInboxRouteInterceptor` checks if the message type is inbox-managed on this channel. If so, it routes the message to the correct `InboxAcceptor` (based on the channel's DbContext), which writes the message and one `InboxHandlerStatus` row per handler to the database. The interceptor returns `SkipDispatch = true` — the `MessageDispatcher` is entirely bypassed for this message.
3. The background `InboxProcessor` polls (and is also woken up via a trigger channel) for pending handler statuses and delivers them in batches.
4. On success: `CompletedAt` is set on the status row. Each handler's result is **persisted immediately** — completed handlers are not lost if a subsequent handler or the process fails.
5. On failure: `ErrorCount` is incremented, `NextAttemptAt` is set using exponential backoff (`2^n` seconds, capped at `MaxRetryDelay`).
6. After `MaxRetries` failures: the status is marked `IsPoisoned = true` and no longer retried (kept for future manual retry).
7. If the application shuts down (cancellation) while a handler is running, the attempt is **not** counted as a failure. The handler status remains in "processing" state and is recovered by stuck message detection on the next startup.
8. For deterministically unrecoverable errors (e.g. `InboxMessage` row deleted, handler key unregistered), the status is poisoned **immediately** without going through the retry cycle.

### Crash safety (local transport)

When using the outbox with local transport and inbox, the `OutboxTriggerInterceptor` writes inbox entries to the database **in the same transaction** as the outbox entry. Combined with the inbox's deduplication, this guarantees no message loss at any crash point in the pipeline.

> **Important:** The direct-to-inbox transaction optimization only works when the outbox and inbox use the **same DbContext type**. If they use different DbContext types, the outbox creates a normal outbox entry for the local transport, and the consumer-side interceptor writes inbox entries separately. This is still safe (the outbox guarantees delivery), but involves two separate transactions instead of one.

> **Important:** When using `PublishDirectAsync` with the local transport, inbox entries are written by the consumer-side `CompositeInboxRouteInterceptor` **after** the message is read from the in-memory channel. This means messages can be lost if the process crashes between the channel write and the consumer processing. For full durability, use the outbox.

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

    // Consume channel — enable inbox per message type
    bus.AddEventConsumeChannel("orders", c =>
    {
        c.UseInbox<AppDbContext>();
        c.Consumes<OrderPlaced>(m => m.UseInbox());     // inbox-managed (all handlers)
        c.Consumes<OrderUpdated>();                       // fire-and-forget (all handlers)
    });

    // Handlers — stable keys required for inbox-managed message types
    bus.AddHandler<OrderPlaced, FulfillmentHandler>("fulfillment");
    bus.AddHandler<OrderPlaced, NotificationHandler>("notification");
    bus.AddHandler<OrderUpdated, AuditLogHandler>();  // not inbox-managed, no key needed
});

services.AddDbContext<AppDbContext>((sp, opts) =>
{
    opts.UseNpgsql("...");
});
```

### Multiple DbContexts

Different consume channels can use different DbContext types for inbox storage. Use `UseInbox<TDbContext>()` on the consume channel to specify which DbContext to use:

```csharp
services.AddRatatoskr(bus =>
{
    bus.UseLocalTransport();

    // Optional: configure per-DbContext options
    bus.UseEfCoreInbox<OrdersDbContext>(inbox => inbox.WithMaxRetries(10));
    bus.UseEfCoreInbox<PaymentsDbContext>();

    // Each channel specifies its DbContext
    bus.AddEventConsumeChannel("orders", c =>
    {
        c.UseInbox<OrdersDbContext>();
        c.Consumes<OrderPlaced>(m => m.UseInbox());
    });

    bus.AddEventConsumeChannel("payments", c =>
    {
        c.UseInbox<PaymentsDbContext>();
        c.Consumes<PaymentReceived>(m => m.UseInbox());
    });

    bus.AddHandler<OrderPlaced, FulfillmentHandler>("fulfillment");
    bus.AddHandler<PaymentReceived, PaymentHandler>("payment");
});
```

Each DbContext type gets its own `InboxProcessor` background service with an independent distributed lock (auto-named `InboxProcessor-{DbContextFullName}`).

Every consume channel that has inbox-managed messages **must** have an explicit `UseInbox<TDbContext>()` call, and every referenced DbContext type **must** be registered via `UseEfCoreInbox<TDbContext>()`. If either is missing, startup fails with an `InvalidOperationException`.

> **Validation**: At startup, the system validates that every channel with inbox-managed messages has an explicit `UseInbox<TDbContext>()` on the channel and that `UseEfCoreInbox<TDbContext>()` was called for that DbContext type. Missing configuration will cause an `InvalidOperationException`.

### 3. Enable inbox per message type

Inbox is configured at the **message level** on consume channels. When `UseInbox()` is called on a message, **all handlers** for that message type are automatically enrolled in the inbox.

```csharp
// Inbox-managed: all handlers go through durable inbox delivery
bus.AddEventConsumeChannel("orders", c => c
    .UseInbox<AppDbContext>()
    .Consumes<OrderPlaced>(m => m.UseInbox()));

// Fire-and-forget: synchronous dispatch, no deduplication
bus.AddEventConsumeChannel("notifications", c =>
    c.Consumes<NotificationSent>());
```

### 4. Assign stable handler keys

Every inbox handler **must** have a stable key. The key is persisted in the database and used as the deduplication and retry key. There are two ways to assign a key:

**Option 1: `[HandlerKey]` attribute** (recommended for most cases)

```csharp
[HandlerKey("fulfillment")]
public class FulfillmentHandler : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, MessageProperties props, CancellationToken ct) => ...;
}

bus.AddHandler<OrderPlaced, FulfillmentHandler>();
```

**Option 2: `AddHandler` parameter** (overrides the attribute if both are set)

```csharp
bus.AddHandler<OrderPlaced, FulfillmentHandler>("fulfillment");
```

If both are provided, the `AddHandler` parameter takes precedence. If neither is provided, startup fails with an `InvalidOperationException`.

> **Why stable keys?** If a handler is renamed or moved to a different namespace, the key must remain the same so that existing in-flight messages continue to be processed. Using an explicit key decouples the identity from the CLR type name.

> **Validation**: At startup, the system validates that every inbox-managed message type has at least one handler registered and that all inbox handlers have a stable key. Missing handlers or keys will cause an `InvalidOperationException`.

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
| `WithLockName(string)` | `"InboxProcessor-{DbContextFullName}"` | Distributed lock name. Auto-generated per DbContext type using the full namespace-qualified name. Override if you need a custom name. |

## Per-Message Inbox Semantics

When `UseInbox()` is set on a message type:

- **All handlers** for that message type are automatically enrolled in the inbox.
- The `MessageDispatcher` is **entirely skipped** for that message (via `SkipDispatch`).
- Each handler must have a stable key (via `[HandlerKey]` attribute or `AddHandler` parameter).

This means you cannot have a mix of inbox-managed and fire-and-forget handlers for the same message type. If you need both durable and fire-and-forget processing for the same event, use separate message types.

You **can** mix inbox-managed and fire-and-forget message types on the same channel:

```csharp
bus.AddEventConsumeChannel("orders", c =>
{
    c.UseInbox<AppDbContext>();
    c.Consumes<OrderPlaced>(m => m.UseInbox());  // durable, per-handler retry
    c.Consumes<OrderUpdated>();                    // fire-and-forget, direct dispatch
});
```

## Deduplication

Deduplication is per **(message ID, handler key)**. If the same CloudEvents `id` is received twice (e.g. RabbitMQ redelivery or outbox retry), the second delivery is a no-op for the inbox: the unique constraint on `(MessageId, HandlerKey)` prevents duplicate handler status rows from being inserted.

> **Note**: The CloudEvents `id` (i.e., `MessageProperties.Id`) must not exceed **200 characters**. Messages with IDs longer than this limit are rejected with an `InvalidOperationException` before the database insert is attempted.

## Distributed Lock Safety

`InboxProcessor` acquires a distributed lock before processing batches to prevent multiple instances from processing the same messages concurrently. If the lock is lost mid-processing (e.g. network partition, Postgres connection drop), the processor detects it immediately via `HandleLostToken` and stops processing. Any in-flight handler status that was being processed will be picked up by stuck message detection on the next cycle.

## RabbitMQ Integration

When using RabbitMQ, the `RabbitMqConsumer` delegates to `MessageRouter`, which calls the `InboxRouteInterceptor`. For inbox-managed messages, the interceptor persists the message and handler statuses, then returns `SkipDispatch = true` — the `MessageDispatcher` never runs for these messages. `InboxProcessor` delivers all handlers independently — failures in inbox-managed handlers no longer affect broker acknowledgement.

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
    bus.AddEventConsumeChannel("orders", c => c
        .UseInbox<AppDbContext>()
        .Consumes<OrderPlaced>(m => m.UseInbox()));
    bus.AddHandler<OrderPlaced, FulfillmentHandler>("fulfillment");
    bus.UseEfCoreInbox<AppDbContext>(inbox => inbox.WithoutBackgroundProcessing());
});

// In the test body, trigger processing manually:
var processor = Services.GetRequiredService<InboxMessageProcessor<AppDbContext>>();
await processor.ProcessBatchAsync(includeStuckMessageDetection: false, CancellationToken.None);
```

## Data Retention

Inbox messages are automatically cleaned up by the `InboxCleanupProcessor` background service based on configurable retention periods:

| Fluent Method | Default | Description |
|---|---|---|
| `WithCompletedRetention(TimeSpan?)` | `7 days` | How long to keep fully completed messages (all handlers succeeded). Set to `null` to disable. |
| `WithPoisonedRetention(TimeSpan?)` | `30 days` | How long to keep poisoned messages (at least one handler poisoned, all terminal). Set to `null` to disable. |
| `WithCleanupInterval(TimeSpan)` | `1 hour` | How often the cleanup processor runs. |
| `WithCleanupBatchSize(int)` | `1000` | Maximum number of messages deleted per batch during cleanup. Large tables are cleaned in batches to avoid long-running transactions. |
| `WithoutCleanup()` | — | Disables all automatic cleanup. |

```csharp
bus.UseEfCoreInbox<AppDbContext>(inbox =>
{
    inbox.WithCompletedRetention(TimeSpan.FromDays(14));  // keep completed for 2 weeks
    inbox.WithPoisonedRetention(TimeSpan.FromDays(90));   // keep poisoned for 3 months
    inbox.WithCleanupInterval(TimeSpan.FromHours(6));     // run every 6 hours
});
```

**Cleanup logic:**

- **Completed messages**: Deleted when ALL handler statuses are completed (none poisoned, none pending) and the message's `ReceivedAt` is older than `CompletedRetention`. Cascade delete on the foreign key removes handler status rows automatically.
- **Poisoned messages**: Deleted when ALL handler statuses are terminal (completed or poisoned), at least one is poisoned, and the message's `ReceivedAt` is older than `PoisonedRetention`.
- **Partially completed messages**: Messages with any still-pending handler status are never cleaned up, regardless of age.

The cleanup processor acquires a distributed lock (`InboxCleanup-{DbContextFullName}`) to prevent concurrent cleanup from multiple instances. The `ratatoskr.inbox.cleanup.count` metric counter tracks deletions by category (`completed` or `poisoned`).

**Multi-DbContext cleanup isolation**: When multiple DbContexts share the same physical database, each `InboxCleanupProcessor` only deletes messages belonging to channels mapped to its own DbContext. This prevents cross-contamination where one DbContext's cleanup could delete another DbContext's messages.

The outbox has matching cleanup options (`WithCompletedRetention`, `WithPoisonedRetention`, `WithCleanupInterval`, `WithCleanupBatchSize`, `WithoutCleanup`) on the `OutboxBuilder`.
