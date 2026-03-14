# Outbox

The transactional outbox pattern solves the dual-write problem: how do you persist business data and publish a message atomically? Without an outbox, a crash between `SaveChangesAsync()` and `PublishAsync()` means the message is lost even though the data was saved.

Ratatoskr's outbox stages messages in the same database transaction as your business data. A background processor then delivers them to the configured transports.

## Setup

### 1. Configure Durability

```csharp
builder.Services.AddRatatoskr(bus =>
{
    bus.AddEfCoreDurability<OrderDbContext>(d =>
    {
        d.UseOutbox();
    });

    bus.AddEventPublishChannel("orders.events", c => c
        .WithRabbitMq(r => r.WithTopicExchange())
        .Produces<OrderPlaced>());
});
```

### 2. Implement `IOutboxDbContext`

Your `DbContext` must implement `IOutboxDbContext` and register outbox entities:

```csharp
public class OrderDbContext(DbContextOptions<OrderDbContext> options)
    : DbContext(options), IOutboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxEntities(Database);
    }
}
```

### 3. Register the Outbox Interceptor

In your `DbContext` configuration, register the outbox interceptor:

[!code-csharp[](../examples/Docs/Program.cs#ConfigureDbContext)]

The `RegisterOutbox<TDbContext>(sp)` call adds an EF Core `SaveChanges` interceptor that processes staged messages during `SaveChangesAsync()`.

## Usage

Stage messages using `OutboxMessages.Add()` and call `SaveChangesAsync()`. The message is persisted in the same transaction as your business data:

[!code-csharp[](../examples/Docs/Program.cs#PublishOutboxExample)]

The `OutboxTriggerInterceptor` hooks into `SaveChangesAsync` to:

1. Enrich the message with CloudEvents properties (ID, timestamp, trace context)
2. Serialize the message body
3. Persist an `OutboxMessageEntity` in the same transaction

## Processing Lifecycle

The `OutboxProcessor` runs as a background `IHostedService`:

1. Acquires a distributed lock (one processor per `DbContext` type across all instances)
2. Queries pending `OutboxMessageEntity` rows in batches
3. Claims each message via optimistic concurrency (`Version` increment)
4. Sends the message to the configured transport via `IMessageSender`
5. On success: marks the message as processed (`ProcessedAt` is set)
6. On failure: increments `ErrorCount` and sets `NextAttemptAt` with exponential backoff

### Same-DbContext Optimization

When the outbox and inbox share the same `DbContext`, the interceptor writes inbox entries (message + handler statuses) directly in the same transaction. No outbox row is created — the inbox processor picks them up immediately. This provides full crash safety with zero indirection.

### Retry and Backoff

Failed messages are retried with exponential backoff:

```
NextAttemptAt = now + min(2^ErrorCount seconds, MaxRetryDelay)

Example with MaxRetryDelay = 5 min:
  Attempt 1 → retry after 2s
  Attempt 2 → retry after 4s
  Attempt 3 → retry after 8s
  ...
  Attempt 9+ → capped at 300s (5 min)
```

### Poisoned Messages

After exceeding `MaxRetries`, the message is marked `IsPoisoned = true` and no longer processed. Poisoned messages remain in the database for investigation. See [Operations](operations.md) for investigation and manual retry procedures.

### Message Size Validation

Configure a maximum message size to prevent oversized messages from being staged:

```csharp
d.UseOutbox(outbox => outbox
    .WithMaxMessageSize(1_048_576)); // 1 MB
```

Messages exceeding this limit cause `SaveChangesAsync` to throw, rolling back the entire transaction (including business data).

## Configuration

```csharp
bus.AddEfCoreDurability<OrderDbContext>(d => d.UseOutbox(outbox =>
{
    outbox.WithMaxRetries(5);
    outbox.WithMaxRetryDelay(TimeSpan.FromMinutes(5));
    outbox.WithPollingInterval(TimeSpan.FromSeconds(60));
    outbox.WithBatchSize(100);
    outbox.WithStuckMessageThreshold(TimeSpan.FromMinutes(5));
    outbox.WithSendTimeout(TimeSpan.FromSeconds(30));
    outbox.WithMaxMessageSize(1_048_576);
    outbox.WithRestartDelay(TimeSpan.FromSeconds(5));
    outbox.WithLockAcquireTimeout(TimeSpan.FromSeconds(60));
    outbox.WithLockName("custom-outbox-lock");
}));
```

| Option | Default | Description |
|--------|---------|-------------|
| `WithMaxRetries(int)` | `5` | Send attempts before marking as poisoned. 0 = poisoned on first failure. |
| `WithMaxRetryDelay(TimeSpan)` | `5 minutes` | Maximum backoff delay between retries |
| `WithPollingInterval(TimeSpan)` | `60 seconds` | How often the processor polls the DB when idle |
| `WithBatchSize(int)` | `100` | Messages processed per batch |
| `WithStuckMessageThreshold(TimeSpan)` | `5 minutes` | Time before a "processing" message is considered stuck |
| `WithSendTimeout(TimeSpan)` | *none* | Maximum time for a send operation. Timeout counts as a failure. |
| `WithMaxMessageSize(int)` | *none* | Maximum serialized body size in bytes. Exceeding throws on `SaveChangesAsync`. |
| `WithRestartDelay(TimeSpan)` | `5 seconds` | Delay before restarting the processor after a crash |
| `WithLockAcquireTimeout(TimeSpan)` | `60 seconds` | Timeout for acquiring the distributed lock |
| `WithLockName(string)` | `"OutboxProcessor_{DbContext}"` | Distributed lock name (auto-generated per DbContext) |

## Data Retention

Configure automatic cleanup of processed messages:

```csharp
d.UseOutbox(outbox => outbox
    .WithRetention(TimeSpan.FromDays(7))
    .WithCleanupInterval(TimeSpan.FromHours(1))
    .WithCleanupBatchSize(10_000));
```

The cleanup service deletes processed messages older than the retention period. **Poisoned messages are never auto-deleted** — they require manual investigation. See [Operations](operations.md) for manual cleanup SQL.

> [!NOTE]
> The cleanup service waits one full `CleanupInterval` before its first run. If enabling retention on a large existing table, use the manual SQL in the [Operations](operations.md) page for the initial cleanup.

## Database Schema

**`OutboxMessageEntity`** — one row per message per transport:

| Column | Description |
|--------|-------------|
| `Id` | Primary key (GUID v7) |
| `Content` | Serialized message body |
| `SerializedProperties` | JSON-encoded MessageProperties |
| `TransportName` | Target transport |
| `CreatedAt` | When the message was staged |
| `ProcessedAt` | When successfully sent (null while pending) |
| `ProcessingStartedAt` | Set during processing, cleared on completion |
| `ErrorCount` | Number of failed attempts |
| `Error` | Last error message |
| `NextAttemptAt` | Exponential backoff timestamp |
| `IsPoisoned` | True after max retries exceeded |
| `Version` | Optimistic concurrency token |

## What's Next

- [Inbox](inbox.md) — Per-handler deduplication and durable delivery
- [Operations](operations.md) — Monitoring, poisoned message investigation, manual retry
- [Architecture](architecture.md) — How the outbox fits into the full pipeline
