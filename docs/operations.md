# Operations

This page covers day-to-day operational concerns: monitoring, handling failures, data retention, distributed lock providers, and deployment considerations.

## Monitoring

### Key Metrics

Ratatoskr exposes OpenTelemetry metrics via `System.Diagnostics.Metrics`. See [Observability](observability.md) for the complete metrics reference and setup.

| Metric | Type | Alert Threshold |
|--------|------|-----------------|
| `ratatoskr.outbox.process.count` | Counter | High failure rate |
| `ratatoskr.outbox.poison.count` | Counter | Any increment |
| `ratatoskr.outbox.batch.size` | Histogram | Sustained high values (backlog) |
| `ratatoskr.outbox.process.duration` | Histogram | > 30s |
| `ratatoskr.inbox.deliver.count` | Counter | High failure rate |
| `ratatoskr.inbox.poison.count` | Counter | Any increment |
| `ratatoskr.inbox.batch.size` | Histogram | Sustained high values (backlog) |
| `ratatoskr.inbox.process.duration` | Histogram | > 30s |
| `ratatoskr.lock.acquisition.failure` | Counter | Sustained failures |
| `ratatoskr.lock.lost` | Counter | Any increment |
| `ratatoskr.receive.lag` | Histogram | Growing lag |
| `ratatoskr.process.lag` | Histogram | Growing lag |

### Critical Alerts

1. **Poison count > 0** — Any poisoned message requires investigation
2. **Lock lost** — Indicates infrastructure issues (database connection drops, network partitions)
3. **Growing backlog** — If batch size consistently equals `BatchSize`, the processor cannot keep up
4. **Receive/process lag trending up** — Throughput is insufficient for message volume

## Handling Poisoned Messages

### Investigation

Poisoned messages have exhausted their retry budget and remain in the database for manual investigation.

**Outbox (PostgreSQL):**
```sql
SELECT "Id", "TransportName", "ErrorCount", "Error", "CreatedAt", "FailedAt"
FROM "OutboxMessages"
WHERE "IsPoisoned" = true
ORDER BY "FailedAt" DESC;
```

**Outbox (SQL Server):**
```sql
SELECT [Id], [TransportName], [ErrorCount], [Error], [CreatedAt], [FailedAt]
FROM [OutboxMessages]
WHERE [IsPoisoned] = 1
ORDER BY [FailedAt] DESC;
```

**Inbox (PostgreSQL):**
```sql
SELECT s."Id", s."MessageId", s."HandlerKey", s."ErrorCount", s."LastError", s."CreatedAt",
       m."SerializedProperties"
FROM "InboxHandlerStatuses" s
JOIN "InboxMessages" m ON m."Id" = s."MessageId"
WHERE s."IsPoisoned" = true
ORDER BY s."CreatedAt" DESC;
```

**Inbox (SQL Server):**
```sql
SELECT s.[Id], s.[MessageId], s.[HandlerKey], s.[ErrorCount], s.[LastError], s.[CreatedAt],
       m.[SerializedProperties]
FROM [InboxHandlerStatuses] s
JOIN [InboxMessages] m ON m.[Id] = s.[MessageId]
WHERE s.[IsPoisoned] = 1
ORDER BY s.[CreatedAt] DESC;
```

### Manual Retry

Reset a poisoned message's state to retry it:

**Outbox (PostgreSQL):**
```sql
UPDATE "OutboxMessages"
SET "IsPoisoned" = false,
    "ErrorCount" = 0,
    "NextAttemptAt" = NULL,
    "ProcessingStartedAt" = NULL,
    "Version" = "Version" + 1
WHERE "Id" = '<message-id>';
```

**Outbox (SQL Server):**
```sql
UPDATE [OutboxMessages]
SET [IsPoisoned] = 0,
    [ErrorCount] = 0,
    [NextAttemptAt] = NULL,
    [ProcessingStartedAt] = NULL,
    [Version] = [Version] + 1
WHERE [Id] = '<message-id>';
```

**Inbox (PostgreSQL):**
```sql
UPDATE "InboxHandlerStatuses"
SET "IsPoisoned" = false,
    "ErrorCount" = 0,
    "NextAttemptAt" = NULL,
    "ProcessingStartedAt" = NULL,
    "Version" = "Version" + 1
WHERE "Id" = '<status-id>';
```

**Inbox (SQL Server):**
```sql
UPDATE [InboxHandlerStatuses]
SET [IsPoisoned] = 0,
    [ErrorCount] = 0,
    [NextAttemptAt] = NULL,
    [ProcessingStartedAt] = NULL,
    [Version] = [Version] + 1
WHERE [Id] = '<status-id>';
```

The processor picks up the message on its next polling cycle.

## Data Retention

### Automatic Cleanup

Configure retention on the outbox and inbox builders. The cleanup service runs as a background `IHostedService` and deletes old processed messages in batches. **Poisoned messages are never auto-deleted.**

```csharp
bus.AddEfCoreDurability<OrderDbContext>(d => d
    .UseOutbox(outbox => outbox
        .WithRetention(TimeSpan.FromDays(7))
        .WithCleanupInterval(TimeSpan.FromHours(1))
        .WithCleanupBatchSize(10_000))
    .UseInbox(inbox => inbox
        .WithRetention(TimeSpan.FromDays(30))));
```

The inbox cleanup also removes orphaned `InboxMessages` rows with no remaining handler statuses.

> [!NOTE]
> The cleanup service waits one full `CleanupInterval` (default: 1 hour) before its first run. Use the manual SQL below for initial cleanup on large existing tables.
> `WithoutBackgroundProcessing()` disables the cleanup service even when `WithRetention()` is configured.

In multi-instance deployments, cleanup services use a distributed lock to ensure only one instance runs cleanup per cycle. Other instances skip the cycle and try again at the next interval. This reduces unnecessary database I/O — cleanup is idempotent, so concurrent execution would be safe but wasteful.

### Manual Cleanup

**Outbox (PostgreSQL):**
```sql
DELETE FROM "OutboxMessages"
WHERE "ProcessedAt" IS NOT NULL
  AND "ProcessedAt" < NOW() - INTERVAL '7 days';

DELETE FROM "OutboxMessages"
WHERE "IsPoisoned" = true
  AND "FailedAt" < NOW() - INTERVAL '30 days';
```

**Outbox (SQL Server):**
```sql
DELETE FROM [OutboxMessages]
WHERE [ProcessedAt] IS NOT NULL
  AND [ProcessedAt] < DATEADD(DAY, -7, GETUTCDATE());

DELETE FROM [OutboxMessages]
WHERE [IsPoisoned] = 1
  AND [FailedAt] < DATEADD(DAY, -30, GETUTCDATE());
```

**Inbox (PostgreSQL):**
```sql
DELETE FROM "InboxHandlerStatuses"
WHERE "CompletedAt" IS NOT NULL
  AND "CompletedAt" < NOW() - INTERVAL '30 days';

DELETE FROM "InboxHandlerStatuses"
WHERE "IsPoisoned" = true
  AND "CreatedAt" < NOW() - INTERVAL '30 days';

DELETE FROM "InboxMessages"
WHERE NOT EXISTS (
    SELECT 1 FROM "InboxHandlerStatuses"
    WHERE "MessageId" = "InboxMessages"."Id"
);
```

**Inbox (SQL Server):**
```sql
DELETE FROM [InboxHandlerStatuses]
WHERE [CompletedAt] IS NOT NULL
  AND [CompletedAt] < DATEADD(DAY, -30, GETUTCDATE());

DELETE FROM [InboxHandlerStatuses]
WHERE [IsPoisoned] = 1
  AND [CreatedAt] < DATEADD(DAY, -30, GETUTCDATE());

DELETE FROM [InboxMessages]
WHERE NOT EXISTS (
    SELECT 1 FROM [InboxHandlerStatuses]
    WHERE [MessageId] = [InboxMessages].[Id]
);
```

For large tables, use batched deletes:

**PostgreSQL:**
```sql
DELETE FROM "OutboxMessages"
WHERE "Id" IN (
    SELECT "Id" FROM "OutboxMessages"
    WHERE "ProcessedAt" IS NOT NULL
      AND "ProcessedAt" < NOW() - INTERVAL '7 days'
    LIMIT 10000
);
```

**SQL Server:**
```sql
DELETE TOP (10000) FROM [OutboxMessages]
WHERE [ProcessedAt] IS NOT NULL
  AND [ProcessedAt] < DATEADD(DAY, -7, GETUTCDATE());
```

## Distributed Lock Provider

Ratatoskr uses [Medallion.Threading](https://github.com/madelson/DistributedLock) for distributed locks. Choose a provider based on your deployment topology.

### Single Machine / Development

```csharp
services.AddSingleton<IDistributedLockProvider>(_ =>
    new FileDistributedSynchronizationProvider(
        new DirectoryInfo("/var/locks/ratatoskr")));
```

### Multi-Instance (PostgreSQL)

```csharp
services.AddSingleton<IDistributedLockProvider>(_ =>
    new PostgresDistributedSynchronizationProvider(connectionString));
```

### Multi-Instance (SQL Server)

```csharp
services.AddSingleton<IDistributedLockProvider>(_ =>
    new SqlDistributedSynchronizationProvider(connectionString));
```

### Multi-Instance (Redis)

```csharp
services.AddSingleton<IDistributedLockProvider>(sp =>
    new RedisDistributedSynchronizationProvider(
        "ratatoskr", sp.GetRequiredService<IDatabase>()));
```

> [!IMPORTANT]
> File-based locks do **not** work across machines. For horizontally scaled deployments, use a database or Redis-backed provider. Without a shared lock provider, multiple processors may run concurrently, causing duplicate message processing.

### Lock Names

Lock names are auto-generated per `DbContext` type:
- Outbox processor: `OutboxProcessor_{DbContextTypeName}`
- Inbox processor: `InboxProcessor_{DbContextTypeName}`
- Outbox cleanup: `OutboxCleanup_{DbContextTypeName}`
- Inbox cleanup: `InboxCleanup_{DbContextTypeName}`

Override with `WithLockName("custom-name")` or `WithCleanupLockName("custom-name")` if needed.

## Disaster Recovery

### Stuck Messages

If a processor crashes mid-batch, messages may be left in "processing" state. Stuck message detection automatically recovers them after the configured threshold (default: 5 minutes).

To manually clear stuck messages:

**PostgreSQL:**
```sql
-- Outbox
UPDATE "OutboxMessages"
SET "ProcessingStartedAt" = NULL, "Version" = "Version" + 1
WHERE "ProcessingStartedAt" IS NOT NULL
  AND "ProcessedAt" IS NULL AND "IsPoisoned" = false;

-- Inbox
UPDATE "InboxHandlerStatuses"
SET "ProcessingStartedAt" = NULL, "Version" = "Version" + 1
WHERE "ProcessingStartedAt" IS NOT NULL
  AND "CompletedAt" IS NULL AND "IsPoisoned" = false;
```

**SQL Server:**
```sql
-- Outbox
UPDATE [OutboxMessages]
SET [ProcessingStartedAt] = NULL, [Version] = [Version] + 1
WHERE [ProcessingStartedAt] IS NOT NULL
  AND [ProcessedAt] IS NULL AND [IsPoisoned] = 0;

-- Inbox
UPDATE [InboxHandlerStatuses]
SET [ProcessingStartedAt] = NULL, [Version] = [Version] + 1
WHERE [ProcessingStartedAt] IS NOT NULL
  AND [CompletedAt] IS NULL AND [IsPoisoned] = 0;
```

### Processor Not Running

If no processor is picking up messages:

1. Check that `WithoutBackgroundProcessing()` is **not** called in production
2. Check distributed lock acquisition — another instance may hold the lock. Monitor `ratatoskr.lock.acquisition.failure`
3. Check database connectivity
4. Check logs for `OutboxProcessor` / `InboxProcessor` at `Warning` and `Error` levels

### RabbitMQ Consumer Disconnection

The `RabbitMqConsumer` automatically reconnects with exponential backoff (1s to 30s with jitter). If persistently disconnected:

1. Check RabbitMQ connectivity and credentials
2. Check consumer logs for error details
3. Verify queue and exchange topology matches configuration

## Graceful Shutdown

On `SIGTERM` or application shutdown:

- The `OutboxProcessor` and `InboxProcessor` stop accepting new batches and wait for the current batch to complete
- The `RabbitMqConsumer` stops consuming and waits for in-flight messages to be acknowledged
- If a handler is running during inbox shutdown, the `CancellationToken` is triggered. The attempt is **not** counted as a failure — it's recovered by stuck message detection on next startup

For rolling deployments, ensure `StuckMessageThreshold` is longer than your shutdown grace period.

## EF Core Migrations

When upgrading Ratatoskr versions, the outbox/inbox database schema may change. Generate a new migration after updating the package:

```bash
dotnet ef migrations add UpgradeRatatoskr
dotnet ef database update
```

Review the generated migration to understand schema changes before applying to production.

## Deployment Safety

### Rolling Deployment Checklist

Before deploying a new version that changes handler configuration:

- [ ] **Handler keys stable** — If renaming a handler key, use legacy keys to drain in-flight messages. See [Inbox: Handler Key Renaming](inbox.md#handler-key-renaming-legacy-keys).
- [ ] **Message types backward-compatible** — Only additive field changes. No renames or removals. See [Architecture: Schema Evolution](architecture.md#schema-evolution).
- [ ] **EF Core migrations applied** — Run `dotnet ef migrations add` and `dotnet ef database update` before deploying the new application version.
- [ ] **Monitoring in place** — Verify `ratatoskr.outbox.poison.count` and `ratatoskr.inbox.poison.count` counters are being collected. A spike after deployment indicates a compatibility issue.

### Monitoring After Deployment

After deploying a new version, monitor these signals for 15-30 minutes:

| Signal | What it means | Action |
|--------|---------------|--------|
| `ratatoskr.inbox.poison.count` spike | Handler key mismatch or deserialization failure | Rollback or investigate poisoned rows |
| `ratatoskr.outbox.poison.count` spike | Transport misconfiguration or serialization mismatch | Rollback or investigate poisoned rows |
| Health check unhealthy | Processor or consumer not running | Check logs for startup errors |
| `ratatoskr.lock.acquisition.failure` spike | Distributed lock contention from old instances | Wait for old instances to drain |

## What's Next

- [Observability](observability.md) — Complete metrics reference and setup
- [Configuration Reference](configuration.md) — All configuration options at a glance
- [Outbox](outbox.md) — Outbox configuration and processing details
- [Inbox](inbox.md) — Inbox configuration and processing details
