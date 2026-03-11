# Operations Runbook

This guide covers day-to-day operational concerns: monitoring, handling failures, data retention, and deployment considerations.

## Monitoring

### Key Metrics

Ratatoskr exposes OpenTelemetry metrics via `System.Diagnostics.Metrics`. Use your preferred metrics backend (Prometheus, Datadog, Azure Monitor, etc.) to collect and alert on these:

| Metric | Type | Description | Alert Threshold |
|---|---|---|---|
| `ratatoskr.outbox.process.count` | Counter | Outbox messages processed (tagged `status=success\|failure`) | High failure rate |
| `ratatoskr.outbox.poison.count` | Counter | Outbox messages permanently failed | Any increment |
| `ratatoskr.outbox.batch.size` | Histogram | Messages per outbox batch | Sustained high values (backlog) |
| `ratatoskr.outbox.process.duration` | Histogram | Duration of outbox batch processing | > 30s |
| `ratatoskr.inbox.deliver.count` | Counter | Inbox handler deliveries attempted (tagged `status`) | High failure rate |
| `ratatoskr.inbox.poison.count` | Counter | Inbox handlers permanently failed | Any increment |
| `ratatoskr.inbox.batch.size` | Histogram | Handler statuses per inbox batch | Sustained high values (backlog) |
| `ratatoskr.inbox.process.duration` | Histogram | Duration of inbox batch processing | > 30s |
| `ratatoskr.lock.acquisition.failure` | Counter | Failed lock acquisitions | Sustained failures |
| `ratatoskr.lock.lost` | Counter | Lock losses during processing | Any increment |
| `ratatoskr.receive.lag` | Histogram | Time from message creation to reception | Growing lag |
| `ratatoskr.process.lag` | Histogram | Time from message creation to processing completion | Growing lag |

### Critical Alerts

Set up alerts for:

1. **Poison count > 0** — Both `ratatoskr.outbox.poison.count` and `ratatoskr.inbox.poison.count`. Any poisoned message requires investigation.
2. **Lock lost** — `ratatoskr.lock.lost` indicates infrastructure issues (database connection drops, network partitions).
3. **Growing backlog** — If `ratatoskr.outbox.batch.size` or `ratatoskr.inbox.batch.size` consistently equals `BatchSize`, the processor cannot keep up.
4. **Receive/process lag trending up** — Indicates throughput is insufficient for message volume.

## Handling Poisoned Messages

### Investigation

Poisoned messages are messages that have exhausted their retry budget. They remain in the database for manual investigation.

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

To retry a poisoned message, reset its state:

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

The processor will pick up the message on its next polling cycle.

## Data Retention

The outbox and inbox tables grow unbounded. Implement periodic cleanup based on your retention policy.

### Outbox Cleanup

**PostgreSQL:**
```sql
-- Delete successfully processed messages older than 7 days
DELETE FROM "OutboxMessages"
WHERE "ProcessedAt" IS NOT NULL
  AND "ProcessedAt" < NOW() - INTERVAL '7 days';

-- Delete poisoned messages older than 30 days (after investigation)
DELETE FROM "OutboxMessages"
WHERE "IsPoisoned" = true
  AND "FailedAt" < NOW() - INTERVAL '30 days';
```

**SQL Server:**
```sql
-- Delete successfully processed messages older than 7 days
DELETE FROM [OutboxMessages]
WHERE [ProcessedAt] IS NOT NULL
  AND [ProcessedAt] < DATEADD(DAY, -7, GETUTCDATE());

-- Delete poisoned messages older than 30 days (after investigation)
DELETE FROM [OutboxMessages]
WHERE [IsPoisoned] = 1
  AND [FailedAt] < DATEADD(DAY, -30, GETUTCDATE());
```

### Inbox Cleanup

**PostgreSQL:**
```sql
-- Delete completed handler statuses older than 30 days
DELETE FROM "InboxHandlerStatuses"
WHERE "CompletedAt" IS NOT NULL
  AND "CompletedAt" < NOW() - INTERVAL '30 days';

-- Delete poisoned statuses older than 30 days (after investigation)
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

**SQL Server:**
```sql
-- Delete completed handler statuses older than 30 days
DELETE FROM [InboxHandlerStatuses]
WHERE [CompletedAt] IS NOT NULL
  AND [CompletedAt] < DATEADD(DAY, -30, GETUTCDATE());

-- Delete poisoned statuses older than 30 days (after investigation)
DELETE FROM [InboxHandlerStatuses]
WHERE [IsPoisoned] = 1
  AND [CreatedAt] < DATEADD(DAY, -30, GETUTCDATE());

-- Delete orphaned inbox messages (no remaining handler statuses)
DELETE FROM [InboxMessages]
WHERE NOT EXISTS (
    SELECT 1 FROM [InboxHandlerStatuses]
    WHERE [MessageId] = [InboxMessages].[Id]
);
```

### Automation

Consider running cleanup as a scheduled job (e.g., cron, Hangfire, or a Kubernetes CronJob). Run during low-traffic windows and use batched deletes to avoid long-running transactions:

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

Ratatoskr uses [Medallion.Threading](https://github.com/madelson/DistributedLock) for distributed locks. The lock provider must be chosen based on your deployment topology.

### Single Machine / Single Instance

File-based locks work for development and single-machine deployments:

```csharp
services.AddSingleton<IDistributedLockProvider>(_ =>
    new FileDistributedSynchronizationProvider(
        new DirectoryInfo("/var/locks/ratatoskr")));
```

### Multi-Instance / Kubernetes

For horizontally scaled deployments, use a database or Redis-backed lock provider:

**PostgreSQL (recommended when you already use PostgreSQL):**
```csharp
services.AddSingleton<IDistributedLockProvider>(_ =>
    new PostgresDistributedSynchronizationProvider(connectionString));
```

**SQL Server:**
```csharp
services.AddSingleton<IDistributedLockProvider>(_ =>
    new SqlDistributedSynchronizationProvider(connectionString));
```

**Redis:**
```csharp
services.AddSingleton<IDistributedLockProvider>(sp =>
    new RedisDistributedSynchronizationProvider("ratatoskr", sp.GetRequiredService<IDatabase>()));
```

> **Important:** File-based locks do NOT work across machines. If you deploy multiple instances, you **must** use a shared lock provider (database or Redis). Failure to do so will result in multiple processors running concurrently, potentially causing duplicate message processing.

### Lock Names

Lock names are auto-generated per DbContext type:
- Outbox: `OutboxProcessor_{DbContextTypeName}`
- Inbox: `InboxProcessor_{DbContextTypeName}`

These are configurable via `WithLockName()` if you need custom names.

## Disaster Recovery

### Stuck Messages

If a processor crashes mid-batch, messages may be left in "processing" state (`ProcessingStartedAt` is set but never cleared). The stuck message detection mechanism automatically recovers these after the configured threshold (default: 5 minutes).

To manually clear stuck messages:

**PostgreSQL:**
```sql
-- Outbox
UPDATE "OutboxMessages"
SET "ProcessingStartedAt" = NULL,
    "Version" = "Version" + 1
WHERE "ProcessingStartedAt" IS NOT NULL
  AND "ProcessedAt" IS NULL
  AND "IsPoisoned" = false;

-- Inbox
UPDATE "InboxHandlerStatuses"
SET "ProcessingStartedAt" = NULL,
    "Version" = "Version" + 1
WHERE "ProcessingStartedAt" IS NOT NULL
  AND "CompletedAt" IS NULL
  AND "IsPoisoned" = false;
```

**SQL Server:**
```sql
-- Outbox
UPDATE [OutboxMessages]
SET [ProcessingStartedAt] = NULL,
    [Version] = [Version] + 1
WHERE [ProcessingStartedAt] IS NOT NULL
  AND [ProcessedAt] IS NULL
  AND [IsPoisoned] = 0;

-- Inbox
UPDATE [InboxHandlerStatuses]
SET [ProcessingStartedAt] = NULL,
    [Version] = [Version] + 1
WHERE [ProcessingStartedAt] IS NOT NULL
  AND [CompletedAt] IS NULL
  AND [IsPoisoned] = 0;
```

### Processor Not Running

If no processor is picking up messages:

1. Check that the `IHostedService` is registered (ensure `WithoutBackgroundProcessing()` is NOT called in production).
2. Check distributed lock acquisition — another instance may hold the lock. Monitor `ratatoskr.lock.acquisition.failure`.
3. Check database connectivity — the processor needs to reach the database for both queries and locks.
4. Check logs for `OutboxProcessor` / `InboxProcessor` — errors are logged at `Warning` and `Error` levels.

### RabbitMQ Consumer Disconnection

The `RabbitMqConsumer` automatically reconnects with exponential backoff (1s to 30s with jitter). If the consumer is persistently disconnected:

1. Check RabbitMQ connectivity and credentials.
2. Check the `RabbitMqConsumer` logs for error details.
3. Verify queue and exchange topology matches the configuration.
