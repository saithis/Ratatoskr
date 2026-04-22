---
title: Fix RabbitMQ concurrency and recovery issues
type: fix
status: completed
date: 2026-04-22
origin: docs/solutions/rabbitmq/rabbitmq-consumer-concurrency-recovery-review-2026-04-22.md
---

# Fix RabbitMQ Concurrency and Recovery Issues

## Overview

Four correctness issues identified in the post-branch review. Two are critical (data-loss risk under
concurrent load), one is a simplification the user explicitly requested, and one is a usability hardening.
Addressed in priority order below.

## Issues

### 1. Ack/nack concurrent access on the consume channel (Critical)

`RabbitMqConsumer` dispatches handlers fire-and-forget with `_ = DispatchAfterGateAsync(...)`. When
`ConcurrencyLimit > 1`, multiple handlers complete concurrently and each calls `BasicAckAsync` /
`BasicNackAsync` / `HandleFailureAsync` on the **same** `IChannel`. The RabbitMQ .NET client requires
acknowledgement operations to be serialized per channel.

Risk: double-ack, channel exceptions, nondeterministic failures under concurrent load.

### 2. Concurrent publish on the shared send channel (Critical)

`RabbitMqMessageSender.SendAsync` retrieves the shared `_sendChannel` via
`connectionManager.GetOrCreateSendChannelAsync(...)` and calls `channel.BasicPublishAsync(...)` without
any serialization. `_sendChannelLock` in `RabbitMqConnectionManager` only protects channel *creation*,
not channel *use*.

Risk: frame interleaving and connection-level protocol errors under parallel publish pressure.

### 3. Custom reconnect loop conflicts with library automatic recovery (High)

`ConnectionFactory` is created with only `Uri` set. The RabbitMQ .NET client has
`AutomaticRecoveryEnabled = true` by default, meaning the library already reconnects,
re-opens channels, and re-registers consumers after a connection drop. The custom reconnect loop
in `RabbitMqConsumer.ExecuteAsync` fights this: both mechanisms attempt recovery simultaneously.

The user has indicated this is acceptable to drop if library recovery is sufficient. It is — with
explicit configuration. Dropping the loop also removes the semantic-error-as-noise problem (issue #6
in the review) because the library does not retry channel-level errors the way the custom loop did.

### 4. Unobserved exceptions from fire-and-forget dispatch (High)

`DispatchAfterGateAsync` is fire-and-forgot. It catches `OperationCanceledException` but any other
unhandled exception propagates to the thread pool as an unobserved task exception. The failure surface
is invisible until observed as a secondary symptom.

## Proposed Solution

### Task 1 — Serialize ack/nack with a per-channel ack lock

Add a `SemaphoreSlim ackLock` (1, 1) for each consume channel alongside the existing `concurrencyGate`.
Thread it through `DispatchAfterGateAsync` → `HandleMessageCoreAsync` → `AcknowledgeResultAsync` and
into `RabbitMqRetryHandler.HandleFailureAsync`.

Only the final ack/nack/reject call needs the lock — handler execution itself can remain concurrent.

**Files to change:**
- [src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs](src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs)
  - `_consumers` tuple: add `SemaphoreSlim AckLock`
  - `ProvisionAndConsumeAsync`: create `new SemaphoreSlim(1, 1)` per channel
  - `DispatchAfterGateAsync`: accept `SemaphoreSlim ackLock`; pass to `HandleMessageCoreAsync`
  - `HandleMessageCoreAsync`: accept `SemaphoreSlim ackLock`; wrap `AcknowledgeResultAsync` call
  - `AcknowledgeResultAsync`: accept `SemaphoreSlim ackLock`; hold it for `BasicAckAsync` duration
  - `CleanupChannelsAsync`: dispose `ackLock` alongside `concurrencyGate`
- [src/Ratatoskr.RabbitMq/RabbitMqRetryHandler.cs](src/Ratatoskr.RabbitMq/RabbitMqRetryHandler.cs)
  - `HandleFailureAsync`: accept `SemaphoreSlim ackLock`; hold it for any `BasicNackAsync` /
    `BasicRejectAsync` / `BasicAckAsync` calls within

### Task 2 — Serialize publish with a send lock

Add a `SemaphoreSlim _publishLock` (1, 1) to `RabbitMqMessageSender`. In `SendAsync`, hold the lock
for the complete `BasicPublishAsync` call (after the channel is obtained, before the publish starts,
released in a `finally`).

Note: RabbitMQ.Client v7 may serialize channel operations internally. Verify the locked version of the
package before deciding whether to keep or remove the lock after confirming behavior. The lock is cheap
if redundant and correct if necessary.

**Files to change:**
- [src/Ratatoskr.RabbitMq/RabbitMqMessageSender.cs](src/Ratatoskr.RabbitMq/RabbitMqMessageSender.cs)
  - Add `private readonly SemaphoreSlim _publishLock = new(1, 1);`
  - In `SendAsync`: wrap `BasicPublishAsync` with `await _publishLock.WaitAsync(cancellationToken)`
    and release in `finally`
  - `DisposeAsync`: dispose `_publishLock`

### Task 3 — Drop custom reconnect loop; configure explicit library recovery

**`RabbitMqConnectionManager.CreateConnectionFactory`** — add explicit recovery settings:

```csharp
return new ConnectionFactory
{
    Uri = options.ConnectionString,
    AutomaticRecoveryEnabled = true,
    TopologyRecoveryEnabled = true,
    NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
    ClientProvidedName = "Ratatoskr",
};
```

**`RabbitMqConsumer.ExecuteAsync`** — replace the reconnect `while` loop with a simple:

```csharp
// Validate config (keep)
...

await ProvisionAndConsumeAsync(stoppingToken);

await Task.Delay(Timeout.Infinite, stoppingToken)
    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

await WaitForInFlightDrainAsync(stoppingToken);
```

**`ProvisionAndConsumeAsync`** — remove the `CancellationTokenSource channelClosedCts` parameter.
Provision topology and register consumers once; the library re-registers consumers after recovery.
`ChannelShutdownAsync` handler becomes logging-only (log Warning with reply code/text).

**Remove entirely:**
- `InitialReconnectDelay`, `MaxReconnectDelay`, `CalculateReconnectDelay`
- `reconnectAttempt` and all backoff logic
- The `while (!stoppingToken.IsCancellationRequested)` loop
- `CleanupChannelsAsync` (channels are managed by the library after recovery; only needed at graceful shutdown)
- The drain-before-reconnect path (drain still happens in `StopAsync` / `WaitForInFlightDrainAsync`)

**`IsHealthy`** — keep as-is; channel open checks remain meaningful. A permanent channel-level error
(e.g., 406 precondition-failed) closes the channel without recovery, making `IsHealthy` return `false`
and surfacing the problem as an unhealthy service rather than a silent retry loop.

**Files to change:**
- [src/Ratatoskr.RabbitMq/RabbitMqConnectionManager.cs](src/Ratatoskr.RabbitMq/RabbitMqConnectionManager.cs)
- [src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs](src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs)

### Task 4 — Log unobserved exceptions from fire-and-forget dispatch

In `DispatchAfterGateAsync`, add a catch-all after the `OperationCanceledException` handler:

```csharp
catch (Exception ex)
{
    logger.LogError(ex, "Unhandled exception dispatching message on channel '{Channel}'", channelName);
}
```

This ensures every failure path is observable without changing the concurrency model.

**Files to change:**
- [src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs](src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs)

## Acceptance Criteria

- [x] `ConcurrencyLimit > 1` consumers do not produce channel exceptions or double-acks under load
- [x] Parallel `SendAsync` calls do not produce protocol errors or frame-interleaving exceptions
- [x] `ConnectionFactory` explicitly declares `AutomaticRecoveryEnabled`, `TopologyRecoveryEnabled`,
  `NetworkRecoveryInterval`, and `ClientProvidedName`
- [x] `ExecuteAsync` no longer contains a reconnect loop; the library handles recovery transparently
- [x] Semantic topology errors (406, 404) result in `IsHealthy = false`, not an infinite retry loop
- [x] All unhandled dispatch exceptions are logged rather than silently swallowed
- [x] All new code paths are covered by integration tests
- [x] Existing shutdown and concurrency tests continue to pass

## Tests

### New: concurrent ack stress test

In `RabbitMqConsumerShutdownTests`:
- Publish N messages with `ConcurrencyLimit = N` and handler that completes immediately
- Assert no channel exceptions are thrown and all messages are acked (queue depth reaches 0)
- Confirms ack serialization under maximum concurrent completion

### New: concurrent publish test

In a new or existing integration test class:
- Fire M `SendAsync` calls in parallel via `Task.WhenAll`
- Assert all succeed without `AlreadyClosedException` or `IOException`
- Confirms publish lock (or library serialization) is effective

### Existing tests that must remain green

- `StopAsync_WaitsForInFlightHandler_BeforeClosingChannels`
- `Consumer_UsesConfiguredConcurrencyLimit_ForParallelHandlers`
- `Consumer_WithPrefetchEqualToConcurrencyLimit_HoldsExtraMessagesInQueue`

## Technical Considerations

**Recovery and topology:** `TopologyRecoveryEnabled = true` re-declares exchanges, queues, bindings,
and re-registers consumers after a connection-level failure. This replaces `ProvisionTopologyAsync`
being called on each reconnect. `ProvisionTopologyAsync` still runs once at startup.

**Channel-level errors are not recovered:** The RabbitMQ .NET client does not reopen channels closed
by the broker with a semantic error code (406 precondition-failed, 404 not-found). `IsHealthy` detects
this state. Consumers wishing to surface this as a hard failure can watch the health check.

**`SemaphoreSlim` vs `Lock` for ack:** Use `SemaphoreSlim(1,1)` not `Lock`/`lock` because the ack
calls are async. `lock` cannot protect async code. `SemaphoreSlim.WaitAsync` is the correct pattern.

**Pub lock scope:** Hold `_publishLock` only for the `BasicPublishAsync` call, not for envelope
mapping or observer notifications, to minimize contention.

## Sources

- **Origin review:** [docs/solutions/rabbitmq/rabbitmq-consumer-concurrency-recovery-review-2026-04-22.md](docs/solutions/rabbitmq/rabbitmq-consumer-concurrency-recovery-review-2026-04-22.md)
  — Key decisions carried forward: ack serialization (finding #2), publish serialization (finding #1),
  drop custom reconnect in favour of explicit library config (finding #3)
- RabbitMQ .NET API guide — concurrency: https://www.rabbitmq.com/client-libraries/dotnet-api-guide#concurrency
- RabbitMQ .NET API guide — automatic recovery: https://www.rabbitmq.com/client-libraries/dotnet-api-guide#recovery
