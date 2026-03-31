---
title: Fix All Remaining Evaluation Findings
type: fix
status: active
date: 2026-03-31
origin: docs/brainstorms/2026-03-31-project-evaluation.md
deepened: 2026-03-31
---

# Fix All Remaining Evaluation Findings

## Enhancement Summary

**Deepened on:** 2026-03-31
**Research agents used:** best-practices-researcher, performance-oracle, security-sentinel, architecture-strategist, data-integrity-guardian, code-simplicity-reviewer

### Key Improvements from Deepening
1. Reduced plan from 43 PRs to ~28 PRs by eliminating YAGNI items and consolidating test/docs PRs
2. Critical security findings: size check must happen BEFORE deserialization, outbox fix needs snapshot-and-restore with change tracker cleanup
3. Concrete implementation patterns from RabbitMQ .NET v7 docs, CloudEvents spec, and reference outbox implementations (MassTransit, Wolverine)

### Items Eliminated After Review
- **PR 3 (A-LOW-10 ProcessorOptionsBase):** Cheap duplication beats wrong abstraction. Only 2 consumers exist — Rule of Three not met.
- **PR 4 (B-SEC-5 HandlerInvokerCache comment):** Code is self-documenting. Fold into next PR touching that file.
- **PR 5 (B-SEC-3 InterBatchDelay):** Zero-default option solving a non-problem. Existing BatchSize + connection pool limits are the real safeguard.
- **PR 8 (A-LOW-1 Per-channel content mode):** YAGNI. TODO in code means it was intentionally deferred. Build when requested.
- **PR 10 (A-MED-7 UseInbox warning):** False positives for every fire-and-forget channel outweigh benefit.

### Items Simplified After Review
- **A-MED-2 (Schema drift):** Replaced full IHostedService with documentation update — schema validation is the consumer's responsibility.
- **A-HIGH-6 (Handler key migration):** Replaced [ObsoleteHandlerKey] attribute with documentation of drain-rename-restart procedure.
- **A-MED-4 (Handler key stability):** Merged with A-HIGH-6 documentation.
- **Section C tests:** Consolidated from 8 PRs to 3 PRs by component area.
- **Section D docs:** Consolidated from 9 PRs to 2 PRs by theme.

---

## Overview

Systematically fix all remaining findings from the Combined Validated Evaluation. Each finding becomes a separate PR with tests and doc updates. Items are ordered from lowest risk/effort to highest.

**Source:** `docs/brainstorms/2026-03-31-project-evaluation.md`

**Already completed:** A-LOW-5, A-PERF-6, A-PERF-5, A-LOW-8, A-MED-9
**Skipped (later):** A-HIGH-5, A-LOW-3, A-LOW-6, A-LOW-7, A-PERF-4, B-SEC-6
**Do last:** A-HIGH-3, A-HIGH-9, A-MED-10, A-MED-11
**Eliminated (YAGNI):** A-LOW-10, B-SEC-3, B-SEC-5, A-LOW-1, A-MED-7
**Ignored:** Accepted Risks, Not Applicable, Outdated Findings

---

## PR 1: A-LOW-9 — Make BackoffCalculator testable by accepting Random parameter

**DONE**

---

## PR 2: A-LOW-4 — Populate `dataschema` attribute on publish path

**DONE**

---

## PR 3: A-PERF-7 — Cache sender lookup in PublishDirectAsync

**DONE**

---

## PR 4: A-LOW-2 — Add dispatch tracing Activity for local transport

**DONE**

---

## PR 5: A-MED-8 + A-MED-4 + A-HIGH-6 — Document message ordering, handler key stability, and rolling deployment safety

**DONE**

---

## PR 6: A-MED-5 — Add distributed lock to cleanup services

**DONE**

**Risk:** Medium | **Effort:** Medium

**Problem:** `InboxCleanupService` and `OutboxCleanupService` run on every instance with no coordination.

**Fix:** Add `IDistributedLockProvider` usage before cleanup runs, matching the `PollingBackgroundService` pattern.

**Files:**
- `src/Ratatoskr.EfCore/Internal/InboxCleanupService.cs` — add distributed lock
- `src/Ratatoskr.EfCore/Internal/OutboxCleanupService.cs` — add distributed lock
- `tests/Ratatoskr.Tests/` — test lock acquisition during cleanup
- `docs/operations.md` — document cleanup distributed locks
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-MED-5 as done

### Research Insights

**Data Integrity Guardian:** This is a resource efficiency issue, NOT a data integrity issue. `ExecuteDeleteAsync` with WHERE conditions is idempotent — concurrent instances deleting overlapping batches won't corrupt data. The lock reduces unnecessary DB I/O in multi-instance deployments. Use `TryAcquireLockAsync` with a short timeout and skip cleanup if lock not acquired.

---

## PR 7: A-MED-6 — Expose health checks via opt-in extension methods

**Risk:** Low | **Effort:** Medium

**Problem:** `RabbitMqConsumerHealthCheck` exists but is never registered. No health checks for processors.

**Fix:** Provide opt-in `IHealthChecksBuilder` extension methods (NOT auto-registration).

**Files:**
- `src/Ratatoskr.RabbitMq/RabbitMqConsumerHealthCheck.cs` — make `public`
- `src/Ratatoskr.RabbitMq/Extensions/HealthCheckExtensions.cs` — new extension methods
- `src/Ratatoskr.EfCore/` — add outbox/inbox processor health checks
- `tests/Ratatoskr.Tests/` — test health check registration and responses
- `docs/operations.md` — document health checks
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-MED-6 as done

### Implementation Details

```csharp
// User opts in explicitly:
services.AddHealthChecks()
    .AddRatatoskrRabbitMq()
    .AddRatatoskrOutbox<AppDbContext>()
    .AddRatatoskrInbox<AppDbContext>();
```

### Research Insights

**Architecture Strategist:** Auto-registration is wrong for a library — can conflict with existing health check setups. The .NET ecosystem convention (`AspNetCore.HealthChecks.RabbitMQ`, `AspNetCore.HealthChecks.NpgSql`) is opt-in extension methods. Keep health check classes `internal`; only extension methods are public.

**Best Practices Researcher:** Use tags for Kubernetes liveness vs readiness mapping:
- `"ready"` tag for readiness probes (consumer channels open, processors running)
- `"live"` tag for liveness probes (process fundamentally healthy)

Track last successful processing time in processor. Health check reports unhealthy if last success exceeds configurable threshold.

---

## PR 8: A-MED-3 + A-MED-2 — Document schema versioning and migration protocol

**Risk:** Very Low | **Effort:** Simple (docs only)

**Problem:** No documentation about message schema versioning or EF Core migration protocol.

**Fix:** Document versioning strategy (additive-only safe, breaking requires drain) and post-upgrade migration checklist.

**Files:**
- `docs/messages-handlers.md` — add "Schema Versioning" section
- `docs/operations.md` — add schema change procedure and post-upgrade migration checklist
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-MED-3 and A-MED-2 as done

### Research Insights

**Code Simplicity Reviewer:** Schema drift detection via IHostedService is the consumer's responsibility, not the library's. A documentation update with a "post-upgrade checklist" is sufficient and costs one paragraph instead of an entire hosted service.

---

## PR 9: A-MED-1 — Add per-channel serializer support (consume-side only)

**Risk:** Medium | **Effort:** Medium

**Problem:** `IMessageSerializer` is a global singleton. No per-channel serializer.

**Fix:** Allow channels to configure a specific serializer for deserialization. The outbox continues using the global serializer.

**Files:**
- `src/Ratatoskr/Config/ChannelBuilder.cs` — add `WithSerializer()` fluent API
- `src/Ratatoskr/Core/ChannelRegistration.cs` — store serializer as extension
- `src/Ratatoskr/Core/MessageDispatcher.cs` — resolve per-channel serializer for deserialization
- `src/Ratatoskr.EfCore/Internal/InboxMessageProcessor.cs` — use per-channel serializer
- `tests/Ratatoskr.Tests/` — test per-channel serializer
- `docs/configuration.md` — document per-channel serializer
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-MED-1 as done

### Research Insights

**Architecture Strategist — critical insight:** The outbox serializes messages at staging time (in `OutboxTriggerInterceptor.SavingChangesAsync`), before the channel is known. A single message can target multiple channels. Per-channel serializer should apply only to the **consume/deserialization path**. The outbox publish path continues using the global serializer. Document this asymmetry explicitly.

Store the serializer on `ChannelRegistration` as an extension (matching the `ChannelInboxConfig` pattern), not as a direct property.

---

## PR 10: A-PERF-1 — Add send channel pool for concurrent publishes

**Risk:** Medium | **Effort:** Medium-High

**Problem:** Single shared AMQP channel behind a semaphore serializes all outgoing publishes.

**Fix:** Implement a channel pool using `System.Threading.Channels.Channel<IChannel>`. Add `SendChannelPoolSize` option (default: 1).

**Files:**
- `src/Ratatoskr.RabbitMq/RabbitMqConnectionManager.cs` — replace single channel with pool
- `src/Ratatoskr.RabbitMq/RabbitMqOptions.cs` — add `SendChannelPoolSize` (default: 1)
- `tests/Ratatoskr.Tests/` — test concurrent publish with pool
- `docs/rabbitmq.md` — document channel pool option
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-PERF-1 as done

### Research Insights

**Performance Oracle:** Use `Channel<IChannel>` (System.Threading.Channels) not `ObjectPool` — ObjectPool lacks async rent/return semantics needed for publisher confirms. Do NOT pre-warm — lazy creation is fine since channel creation over an existing connection is sub-millisecond. Default pool size 1 for backward compat; recommend 2-5 for high throughput.

**Best Practices Researcher:** RabbitMQ .NET v7 channels use internal `Channel` classes with dedicated threads, making them thread-safe for concurrent operations. A pool of 2-5 channels suffices for very high throughput. Always validate `IsOpen` before reuse. If underlying connection drops, drain and recreate all pooled channels.

---

## PR 11: A-PERF-2 — Batch SaveChangesAsync in outbox processor only

**Risk:** Medium | **Effort:** Medium

**Problem:** Each outbox message triggers a separate `SaveChangesAsync` — 100 DB round-trips per batch.

**Fix:** Accumulate status changes and flush every N items. Use optimistic-batch-with-fallback strategy. **Apply to outbox only** — inbox keeps per-handler saves.

**Files:**
- `src/Ratatoskr.EfCore/Internal/OutboxMessageProcessor.cs` — batch saves
- `src/Ratatoskr.EfCore/OutboxOptions.cs` — add `SaveBatchSize` option (default: 5)
- `tests/Ratatoskr.Tests/` — test batched saving and fallback on concurrency exception
- `docs/configuration.md` — document SaveBatchSize
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-PERF-2 as done

### Research Insights

**Data Integrity Guardian — critical insight:** Do NOT batch across handler invocations in the inbox processor. Handler failure mid-batch leaves prior completed statuses unsaved. Keep per-handler saves in inbox; apply batching only to outbox where the pattern is simpler.

**Performance Oracle:** Strategy: process N messages, call `SaveChangesAsync` once. On `DbUpdateConcurrencyException`, fall back to individual saves for that batch only. Conservative default `SaveBatchSize` of 5 reduces blast radius. **Must implement after PR 16 (A-CRIT-1 outbox staging fix)** to avoid amplifying the failure window.

---

## PR 12: A-PERF-3 — Add consumer concurrency option

**Risk:** Medium | **Effort:** Medium

**Problem:** No explicit concurrency option for message consumption.

**Fix:** Add `ConcurrencyLimit` to `RabbitMqChannelOptions`. Use `SemaphoreSlim` for concurrent handler invocations.

**Files:**
- `src/Ratatoskr.RabbitMq/Config/RabbitMqChannelOptions.cs` — add `ConcurrencyLimit`
- `src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs` — implement concurrent dispatch with SemaphoreSlim
- `tests/Ratatoskr.Tests/` — test concurrent message handling
- `docs/rabbitmq.md` — document concurrency option
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-PERF-3 as done

### Research Insights

**Performance Oracle — critical:** `PrefetchCount` must be >= `ConcurrencyLimit`. Add startup validation: if `ConcurrencyLimit > PrefetchCount`, either auto-adjust `PrefetchCount` or throw configuration error. Default `PrefetchCount` is 10 in `RabbitMqChannelOptions`.

**Security Sentinel:** With concurrent handlers, ensure ack/nack per delivery tag is not interleaved unsafely. RabbitMQ.Client v7 channel operations are thread-safe, so this is fine. The inbox acceptor already creates its own scope per message.

Track in-flight message count with `Interlocked.Increment`/`Decrement` for graceful shutdown (PR 15).

---

## PR 13: A-HIGH-1 + A-HIGH-2 — Add inbound and inbox body size limits

**Risk:** Medium | **Effort:** Medium

**Problem:** No body size validation on inbound path or inbox persistence.

**Fix:** Add `MaxInboundMessageSize` to `RabbitMqOptions` (checked BEFORE deserialization) and `MaxMessageSize` to `InboxOptions`.

**Files:**
- `src/Ratatoskr.RabbitMq/RabbitMqOptions.cs` — add `MaxInboundMessageSize` (default: null = no limit)
- `src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs` — check `ea.Body.Length` BEFORE `envelopeMapper.MapIncoming()`
- `src/Ratatoskr.EfCore/InboxOptions.cs` — add `MaxMessageSize` (default: null)
- `src/Ratatoskr.EfCore/Internal/InboxAcceptor.cs` — check size BEFORE entity creation
- `tests/Ratatoskr.Tests/` — test oversized message rejection
- `docs/configuration.md` — document size limits
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-HIGH-1 and A-HIGH-2 as done

### Research Insights

**Security Sentinel — CRITICAL:** Size check MUST happen at the transport layer on raw `ea.Body.Length` BEFORE calling `envelopeMapper.MapIncoming()`. In structured mode, `MapStructuredModeIncoming` fully deserializes the CloudEvents envelope including embedded data — a 3x memory amplification. A single malicious large message can cause OOM.

```csharp
// In HandleMessageAsync, BEFORE envelopeMapper.MapIncoming():
if (options.MaxInboundMessageSize.HasValue && ea.Body.Length > options.MaxInboundMessageSize.Value)
{
    logger.LogWarning("Rejecting oversized message: {Size} bytes exceeds limit of {Limit}",
        ea.Body.Length, options.MaxInboundMessageSize.Value);
    await channel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken); // no requeue
    return;
}
```

**Data Integrity Guardian:** Default to `null` (no limit) to avoid migration hazard with existing queued messages. When rejecting, use `BasicNackAsync` with `requeue: false` to prevent infinite requeue loops. Document the deployment sequence: ensure producers respect limit → drain existing queues → enable limit on consumers.

---

## PR 14: A-HIGH-4 — Add backlog-depth gauge metrics

**Risk:** Low | **Effort:** Medium

**Problem:** No `ObservableGauge` for pending outbox/inbox rows. Operators can't monitor queue depth.

**Fix:** Add `ObservableGauge` metrics with cached values updated by a background timer.

**Files:**
- `src/Ratatoskr/Core/RatatoskrDiagnostics.cs` — add gauge metric definitions
- `src/Ratatoskr.EfCore/Internal/BacklogMetricsCollector.cs` — new background timer that polls DB counts
- `src/Ratatoskr.EfCore/PublicApiExtensions.cs` — register the collector
- `tests/Ratatoskr.Tests/` — test gauge values
- `docs/observability.md` — document new gauge metrics
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-HIGH-4 as done

### Metrics to add:
- `ratatoskr.outbox.pending` — ObservableGauge: pending outbox messages
- `ratatoskr.inbox.pending` — ObservableGauge: pending inbox handler statuses

### Research Insights

**Best Practices Researcher — critical:** Observable callbacks MUST be fast. Never query DB in the gauge callback — cache the value with a background timer:

```csharp
internal class BacklogMetricsCollector<TDbContext> : IDisposable
{
    private long _pendingCount;
    private readonly Timer _pollTimer;

    public BacklogMetricsCollector(IServiceScopeFactory scopeFactory)
    {
        _pollTimer = new Timer(async _ =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
            var count = await db.Set<OutboxMessageEntity>()
                .CountAsync(m => m.ProcessedAt == null && !m.IsPoisoned);
            Volatile.Write(ref _pendingCount, count);
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }
}
```

**Architecture Strategist:** Create dedicated `IServiceScope` per callback — processor scopes are transient and disposed immediately after each batch. Use `IMeterFactory` (not static `Meter`) for DI integration and testability.

---

## PR 15: A-HIGH-7 — Add graceful consumer shutdown with drain

**Risk:** Medium-High | **Effort:** Medium

**Problem:** `RabbitMqConsumer.StopAsync` doesn't wait for in-flight handlers before closing channels.

**Fix:** Cancel consumer tags → wait for in-flight handlers → close channels.

**Files:**
- `src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs` — implement drain-then-close + track consumer tags + in-flight counter
- `src/Ratatoskr.RabbitMq/RabbitMqOptions.cs` — add `ShutdownDrainTimeout` (default: 30s)
- `tests/Ratatoskr.Tests/` — test graceful shutdown
- `docs/rabbitmq.md` — document shutdown behavior
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-HIGH-7 as done

### Implementation Details

```csharp
public override async Task StopAsync(CancellationToken cancellationToken)
{
    // 1. Cancel all consumer subscriptions (stop new deliveries)
    await CancelConsumersAsync(cancellationToken);

    // 2. Wait for in-flight handlers to complete
    await base.StopAsync(cancellationToken);

    // 3. Close channels (all in-flight work done)
    await CleanupChannelsAsync();
}
```

### Research Insights

**Best Practices Researcher:** Track consumer tags returned by `BasicConsumeAsync`. Track in-flight count with `Interlocked.Increment`/`Decrement` in `HandleMessageAsync`. Wait for count to reach zero in `StopAsync`. Default .NET `HostOptions.ShutdownTimeout` is 30s — document that consumers taking longer should configure it.

---

## PR 16: A-CRIT-1 — Fix outbox staging queue message loss on SaveChanges failure

**Risk:** Critical fix | **Effort:** Medium

**Problem:** `OutboxTriggerInterceptor.SavingChangesAsync` dequeues items before DB commit. If `SaveChangesAsync` fails, messages are lost.

**Fix:** Snapshot-and-restore approach with change tracker cleanup on failure.

**Files:**
- `src/Ratatoskr.EfCore/Internal/OutboxTriggerInterceptor.cs` — snapshot queue, restore on failure, clear change tracker
- `tests/Ratatoskr.Tests/` — test message recovery after SaveChanges failure AND retry succeeds with exactly one outbox row
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-CRIT-1 as done

### Implementation Details

**Data Integrity Guardian + Architecture Strategist (converged approach):**

```csharp
private sealed class StagedFlags
{
    public bool OutboxEntitiesStaged;
    public bool InboxEntitiesStaged;
    public List<OutboxStagingCollection.Item>? StagedItems; // snapshot for restore
}
```

1. **`SavingChangesAsync`:** Drain queue into `StagedFlags.StagedItems` list. Clear the queue. Process items and add entities as before.
2. **`SavedChangesAsync`:** Discard `StagedItems` (items successfully committed). Trigger processors.
3. **`SaveChangesFailedAsync` (NEW override):** Re-enqueue `StagedItems` back into the queue. **Detach outbox/inbox entities from the change tracker** to prevent double-insertion on retry.

```csharp
public override Task SaveChangesFailedAsync(
    DbContextErrorEventData eventData,
    CancellationToken cancellationToken = default)
{
    if (eventData.Context != null && _perContextFlags.TryGetValue(eventData.Context, out var flags))
    {
        // Re-enqueue staged items
        if (flags.StagedItems != null && eventData.Context is IOutboxDbContext outboxCtx)
        {
            foreach (var item in flags.StagedItems)
                outboxCtx.OutboxMessages.Queue.Enqueue(item);
        }

        // Detach outbox/inbox entities from change tracker to prevent double-insertion
        foreach (var entry in eventData.Context.ChangeTracker.Entries<OutboxMessageEntity>())
            entry.State = EntityState.Detached;
        foreach (var entry in eventData.Context.ChangeTracker.Entries<InboxMessageEntity>())
            entry.State = EntityState.Detached;
        foreach (var entry in eventData.Context.ChangeTracker.Entries<InboxHandlerStatusEntity>())
            entry.State = EntityState.Detached;

        _perContextFlags.Remove(eventData.Context);
    }
    return Task.CompletedTask;
}
```

### Research Insights

**Security Sentinel — CRITICAL race condition warning:** Re-entrance protection needed. If `StagedFlags.StagedItems != null` when `SavingChangesAsync` fires, a previous save is still pending — throw instead of silently overwriting.

**Data Integrity Guardian:** After a failed `SaveChangesAsync`, the change tracker still holds the `.Add()`-ed entities. Without detaching them, a retry produces duplicate outbox rows. **Test this explicitly:** stage a message, force `SaveChangesAsync` to fail, verify message is back in queue, call `SaveChangesAsync` again successfully, verify exactly one outbox row.

**MassTransit/Wolverine reference:** Both frameworks write messages to the DB in the same transaction as business data. Dispatch to broker happens AFTER commit succeeds via background processor. This matches Ratatoskr's approach. The key fix is making queue dequeue conditional on commit success.

---

## PR 17: A-CRIT-2 — Add configurable mandatory flag for RabbitMQ publishes

**Risk:** Critical fix | **Effort:** Medium

**Problem:** Both `RabbitMqMessageSender` and `RabbitMqRetryHandler` publish with `mandatory: false`. Unroutable messages silently discarded.

**Fix:** Add `MandatoryPublish` option (default: `false`). DLQ publishes always use `mandatory: true`.

**Files:**
- `src/Ratatoskr.RabbitMq/RabbitMqOptions.cs` — add `MandatoryPublish` (default: false)
- `src/Ratatoskr.RabbitMq/RabbitMqMessageSender.cs` — use option for mandatory flag
- `src/Ratatoskr.RabbitMq/RabbitMqRetryHandler.cs` — always use `mandatory: true` for DLQ publishes
- `src/Ratatoskr/Core/RatatoskrDiagnostics.cs` — add `ratatoskr.message.unroutable.count` counter
- `tests/Ratatoskr.Tests/` — test mandatory publish behavior
- `docs/rabbitmq.md` — document MandatoryPublish option and when to use it
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-CRIT-2 as done

### Research Insights

**Best Practices Researcher:** With publisher confirms enabled (the default), `BasicPublishAsync` throws `PublishException` automatically for unroutable mandatory messages — no separate `BasicReturn` handler needed. Without confirms, register `channel.BasicReturnAsync` handler.

**Security Sentinel — HIGH:** DLQ publishes (line 89 in `RabbitMqRetryHandler`) should ALWAYS use `mandatory: true` regardless of global setting. Losing dead-lettered messages is a double data loss — original processing failed AND the DLQ publish was silently dropped.

---

## PR 18: A-CRIT-3 — Reject inbox messages without stable ID

**Risk:** Critical fix | **Effort:** Simple

**Problem:** Binary-mode mapper falls back to `Guid.NewGuid()` when no message ID present, defeating inbox deduplication.

**Fix:** Set `Id = null` when no ID found (don't generate). Inbox acceptor already rejects null IDs.

**Files:**
- `src/Ratatoskr.RabbitMq/CloudEventsAmqpMapper.cs` — remove `Guid.NewGuid()` fallback, set `Id = null`
- `tests/Ratatoskr.Tests/` — test that ID-less messages on inbox channels are rejected, non-inbox channels still work
- `docs/inbox.md` — document stable ID requirement
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-CRIT-3 as done

### Implementation Details

In `MapBinaryModeIncoming` (line 200-202), change:
```csharp
// Before:
var id = incoming.BasicProperties.MessageId
         ?? GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.IdHeader)
         ?? Guid.NewGuid().ToString();

// After:
var id = incoming.BasicProperties.MessageId
         ?? GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.IdHeader);
```

### Research Insights

**Data Integrity Guardian:** Non-inbox channels work fine with null IDs — `MessageProperties.Id` is only required by the inbox path. The consumer's local `messageId` variable at `RabbitMqConsumer.cs` line 186 independently computes a GUID for logging — separate from `MessageProperties`. No behavioral change for consumer log lines.

**CloudEvents spec:** The `id` field is REQUIRED per spec. Messages without it are non-conformant. The inbox correctly rejects them. No need for an `IsIdAutoGenerated` flag — the contract is simple: inbox requires stable producer-assigned IDs.

---

## PR 19: Test coverage gaps (consolidated — T1-T8)

### PR 19a: Outbox test gaps (T1, T2, T5)
- T1: Test outbox `MaxMessageSize` validation — oversized messages throw during SaveChanges
- T2: Test outbox concurrent processor contention — `DbUpdateConcurrencyException` handling
- T5: Test `OutboxStagingCollection.Add(object)` non-generic overload

### PR 19b: Inbox test gaps (T4, T6, T7, T8)
- T4: Test inbox `ReceivedAt` timestamp correctness — verify uses TimeProvider
- T6: Test `InboxMessageProcessor` missing message record — handler status with deleted message
- T7: Test inbox `SerializedProperties` deserialization failure poisoning
- T8: Improve concurrent deduplication test reliability — use barriers/semaphores

### PR 19c: Infrastructure test gap (T3)
- T3: Test `PollingBackgroundService` distributed lock loss handling

**Files:** `tests/Ratatoskr.Tests/Integration/` — organized by component
**Docs:** `docs/brainstorms/2026-03-31-project-evaluation.md` — mark T1-T8 as done

---

## PR 20: Non-functional documentation (consolidated — Section D + A-HIGH-3)

### PR 20a: Operational documentation
- D-NF-1: Ordering and causality semantics
- D-NF-5: Capacity and failure-mode expectations
- D-NF-6: Data governance and PII retention guidance
- D-NF-7: Upgrade and rollback procedure

**Files:** `docs/operations.md`, `docs/architecture.md`

### PR 20b: Design and security documentation
- D-NF-2: Idempotency ownership per handler
- D-NF-3: Schema governance lifecycle
- A-HIGH-3: Wire metadata trust boundary (document TLS + broker ACLs as mitigation)

**Files:** `docs/architecture.md`, `docs/inbox.md`, `docs/messages-handlers.md`
**Docs:** `docs/brainstorms/2026-03-31-project-evaluation.md` — mark all as done

---

## Do Last

### PR 21: A-HIGH-9 — Document multi-tenancy non-support
**Files:** `docs/architecture.md` — add multi-tenancy section

### PR 22: A-MED-10 — Document hot-reload non-support
**Files:** `docs/configuration.md` — add note about frozen config

### PR 23: A-MED-11 — Document transport abstraction coupling
**Files:** `docs/architecture.md` — add transport coupling section

These three are trivial docs-only PRs. Can be combined into a single PR.

---

## Implementation Order

**Recommended order based on risk and dependencies:**

1. PR 1 (A-LOW-9) — BackoffCalculator testability
2. PR 2 (A-LOW-4) — dataschema attribute
3. PR 3 (A-PERF-7) — sender lookup cache
4. PR 4 (A-LOW-2) — dispatch tracing
5. PR 5 (docs: A-MED-8/A-MED-4/A-HIGH-6) — ordering + handler key docs
6. PR 6 (A-MED-5) — cleanup distributed locks
7. PR 7 (A-MED-6) — health checks
8. PR 8 (docs: A-MED-3/A-MED-2) — schema versioning + migration docs
9. PR 9 (A-MED-1) — per-channel serializer
10. PR 10 (A-PERF-1) — send channel pool
11. PR 11 (A-PERF-2) — batch saves (AFTER PR 16)
12. PR 12 (A-PERF-3) — consumer concurrency
13. PR 13 (A-HIGH-1/A-HIGH-2) — body size limits
14. PR 14 (A-HIGH-4) — backlog gauge metrics
15. PR 15 (A-HIGH-7) — graceful shutdown
16. **PR 16 (A-CRIT-1) — outbox staging fix** ← do before PR 11
17. PR 17 (A-CRIT-2) — mandatory publish
18. PR 18 (A-CRIT-3) — stable ID requirement
19. PR 19a/b/c — test coverage gaps
20. PR 20a/b — non-functional documentation
21. PR 21-23 — do-last docs

**Critical dependency:** PR 16 (A-CRIT-1 outbox staging fix) must be done BEFORE PR 11 (A-PERF-2 batch saves) to avoid amplifying the failure window.

---

## Acceptance Criteria

- [ ] Each finding has its own PR with passing tests
- [ ] Each PR marks its finding as done in the evaluation document
- [ ] Documentation is updated for each code change
- [ ] All existing tests continue to pass
- [ ] No breaking changes to public API without justification

## Sources & References

- **Origin brainstorm:** [docs/brainstorms/2026-03-31-project-evaluation.md](docs/brainstorms/2026-03-31-project-evaluation.md)
- **Previous plan:** [docs/plans/2026-03-31-001-low-hanging-fruit-evaluation-fixes.md](docs/plans/2026-03-31-001-low-hanging-fruit-evaluation-fixes.md)
- **CloudEvents Specification:** https://github.com/cloudevents/spec/blob/main/cloudevents/spec.md
- **RabbitMQ .NET Client v7 API Guide:** https://www.rabbitmq.com/client-libraries/dotnet-api-guide
- **MassTransit Outbox Pattern:** https://masstransit.io/documentation/patterns/transactional-outbox
- **OpenTelemetry Messaging Metrics:** https://opentelemetry.io/docs/specs/semconv/messaging/messaging-metrics/
- **.NET Health Checks:** https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks
- **.NET Metrics Instrumentation:** https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation
