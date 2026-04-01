# Plan: Low-Hanging Fruit from Combined Validated Evaluation

**Date:** 2026-03-31
**Source:** `docs/brainstorms/2026-03-31-project-evaluation.md`
**Approach:** Each item becomes a separate PR with tests and docs updates. Items are ordered by simplicity and risk (easiest first).

---

## PR 1: A-LOW-5 — Add OrderBy to inbox orphan cleanup query

**Risk:** Very Low | **Effort:** Trivial (1 line)

**Problem:** `InboxCleanupService` orphan cleanup uses `Take(CleanupBatchSize)` without `OrderBy`, causing non-deterministic batch selection. The completed-message cleanup in the same file correctly uses `.OrderBy(x => x.CompletedAt)`.

**Fix:** Add `.OrderBy(m => m.ReceivedAt)` before `.Take()` on the orphan cleanup query (line ~68 in `InboxCleanupService.cs`).

**Files:**
- `src/Ratatoskr.EfCore/Internal/InboxCleanupService.cs` — add OrderBy
- `tests/Ratatoskr.Tests/` — add test verifying orphan cleanup works correctly
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-LOW-5 as done

### Implementation Details

#### Exact code change

**File:** `src/Ratatoskr.EfCore/Internal/InboxCleanupService.cs`, line 68-70

Before:
```csharp
deleted = await dbContext.Set<InboxMessageEntity>()
    .Where(m => !dbContext.Set<InboxHandlerStatusEntity>().Any(s => s.MessageId == m.Id))
    .Take(_options.CleanupBatchSize)
    .ExecuteDeleteAsync(cancellationToken);
```

After:
```csharp
deleted = await dbContext.Set<InboxMessageEntity>()
    .Where(m => !dbContext.Set<InboxHandlerStatusEntity>().Any(s => s.MessageId == m.Id))
    .OrderBy(m => m.ReceivedAt)
    .Take(_options.CleanupBatchSize)
    .ExecuteDeleteAsync(cancellationToken);
```

#### Test strategy

**File:** `tests/Ratatoskr.Tests/Integration/Inbox/InboxCleanupServiceTests.cs` — add a new test method.

The existing `InboxCleanupServiceTests` already has 6 tests covering cleanup behavior. Add one test:

- `Cleanup_OrphanedMessages_DeletesOldestFirst` — Insert multiple orphaned messages (messages with no handler statuses) created at different times. Use a `CleanupBatchSize` of 1, call `CleanupAsync`, and assert that only the oldest (earliest `ReceivedAt`) is deleted. This verifies the `OrderBy(m => m.ReceivedAt)` clause is effective.

The test follows the existing pattern: extend `InboxTestBase`, use `FakeTimeProvider`, call `CleanupAsync` directly, and use AwesomeAssertions (`Should()`) for assertions. Create orphan messages by inserting `InboxMessageEntity` rows without corresponding `InboxHandlerStatusEntity` rows.

#### Docs to update

No feature docs mention the ordering behavior of cleanup queries. Only mark the evaluation doc as done:
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-LOW-5 as resolved

---

## PR 2: A-PERF-6 — Cache GetProperties() deserialization in entity classes

**Risk:** Very Low | **Effort:** Simple

**Problem:** `OutboxMessageEntity.GetProperties()` and `InboxMessageEntity.GetProperties()` call `JsonSerializer.Deserialize` on every invocation. In the outbox processor, the entity is accessed multiple times per message.

**Fix:** Add a `private MessageProperties? _cachedProperties` field and cache the result on first call.

**Files:**
- `src/Ratatoskr.EfCore/Internal/OutboxMessageEntity.cs` — cache GetProperties()
- `src/Ratatoskr.EfCore/Internal/InboxMessageEntity.cs` — cache GetProperties()
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-PERF-6 as done

### Implementation Details

#### Exact code changes

**File:** `src/Ratatoskr.EfCore/Internal/OutboxMessageEntity.cs`, lines 57-59

Before:
```csharp
public MessageProperties GetProperties() =>
    JsonSerializer.Deserialize<MessageProperties>(SerializedProperties)
    ?? throw new OutboxMessageSerializationException("Could not deserialize the message properties.", SerializedProperties);
```

After:
```csharp
private MessageProperties? _cachedProperties;

public MessageProperties GetProperties() =>
    _cachedProperties ??= JsonSerializer.Deserialize<MessageProperties>(SerializedProperties)
    ?? throw new OutboxMessageSerializationException("Could not deserialize the message properties.", SerializedProperties);
```

**File:** `src/Ratatoskr.EfCore/Internal/InboxMessageEntity.cs`, lines 25-27

Before:
```csharp
public MessageProperties GetProperties() =>
    JsonSerializer.Deserialize<MessageProperties>(SerializedProperties)
    ?? throw new InvalidOperationException($"Could not deserialize MessageProperties for inbox message '{Id}'.");
```

After:
```csharp
private MessageProperties? _cachedProperties;

public MessageProperties GetProperties() =>
    _cachedProperties ??= JsonSerializer.Deserialize<MessageProperties>(SerializedProperties)
    ?? throw new InvalidOperationException($"Could not deserialize MessageProperties for inbox message '{Id}'.");
```

#### Test strategy

These entities are internal and not directly unit-testable from the test project. The caching is purely a performance optimization that does not change observable behavior. Existing integration tests in `OutboxCleanupServiceTests`, `InboxCleanupServiceTests`, and the various `InboxBasicProcessingTests` / `OutboxProcessingTests` already exercise `GetProperties()` through the processor pipeline — they will serve as regression coverage.

No new tests are needed, but verify existing tests pass to confirm the caching does not break anything.

#### Docs to update

No docs reference `GetProperties()`. Only mark the evaluation doc:
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-PERF-6 as resolved

---

## PR 3: A-PERF-5 — Cache AsyncAPI document generation

**Risk:** Low | **Effort:** Simple

**Problem:** `AsyncApiEndpointExtensions.MapAsyncApiEndpoint` calls `generator.Generate()` on every HTTP request. The AsyncAPI document is deterministic at runtime (channels/messages are fixed at startup).

**Fix:** Use `Lazy<T>` or a cached string in the endpoint to generate the document once.

**Files:**
- `src/Ratatoskr/AsyncApi/Extensions/AsyncApiEndpointExtensions.cs` — cache generation
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-PERF-5 as done

### Implementation Details

#### Exact code change

**File:** `src/Ratatoskr/AsyncApi/Extensions/AsyncApiEndpointExtensions.cs`, lines 24-39

Before:
```csharp
public static IEndpointRouteBuilder MapAsyncApi(
    this IEndpointRouteBuilder endpoints,
    string routePattern = "/asyncapi.json")
{
    endpoints.MapGet(routePattern, (AsyncApiDocumentGenerator generator) =>
    {
        var document = generator.Generate();
        var json = JsonSerializer.Serialize(document, _serializerOptions);
        return Results.Content(json, "application/json");
    })
    .WithName("asyncapi")
    .WithDisplayName("AsyncAPI Document")
    .ExcludeFromDescription(); // exclude from Swagger UI if present

    return endpoints;
}
```

After:
```csharp
public static IEndpointRouteBuilder MapAsyncApi(
    this IEndpointRouteBuilder endpoints,
    string routePattern = "/asyncapi.json")
{
    string? cachedJson = null;

    endpoints.MapGet(routePattern, (AsyncApiDocumentGenerator generator) =>
    {
        cachedJson ??= JsonSerializer.Serialize(generator.Generate(), _serializerOptions);
        return Results.Content(cachedJson, "application/json");
    })
    .WithName("asyncapi")
    .WithDisplayName("AsyncAPI Document")
    .ExcludeFromDescription(); // exclude from Swagger UI if present

    return endpoints;
}
```

The `cachedJson` variable is captured by the lambda closure. Since `AsyncApiDocumentGenerator` is registered as singleton and the channel registry is frozen at startup, the result is deterministic. The `??=` assignment is safe even without locking because duplicate serializations produce identical strings (worst case: a few redundant computations on first concurrent requests).

#### Test strategy

**File:** `tests/Ratatoskr.Tests/AsyncApi/AsyncApiDocumentGeneratorTests.cs` — existing snapshot tests (`Generate_BinaryMode_PublishAndConsumeChannels_WithRabbitMqBindings`, etc.) already verify the generation output. The caching is transparent — same output, just computed once.

No new tests needed. Existing tests cover correctness; this is a pure performance change.

#### Docs to update

- `docs/asyncapi.md` — No change needed. The docs say "generated from your channel configuration at runtime" which remains accurate (it is still generated at runtime, just once).
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-PERF-5 as resolved

---

## PR 4: A-LOW-8 — Source-generated logging in InboxMessageProcessor

**Risk:** Low | **Effort:** Medium (mechanical)

**Problem:** `InboxMessageProcessor` uses direct `logger.LogXxx()` calls while `OutboxMessageProcessor` uses source-generated `[LoggerMessage]` attributes. Inconsistency increases maintenance surface and inbox logging incurs boxing overhead.

**Fix:** Create a `partial class InboxMessageProcessorLog` with `[LoggerMessage]` attributes, mirroring the pattern in `OutboxMessageProcessor`.

**Files:**
- `src/Ratatoskr.EfCore/Internal/InboxMessageProcessor.cs` — convert to source-generated logging
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-LOW-8 as done

### Implementation Details

#### Reference pattern

`OutboxMessageProcessor.cs` lines 216-241 define `internal static partial class OutboxMessageProcessorLog` with 8 `[LoggerMessage]` methods. Each direct `logger.LogXxx()` call in the processor is replaced with a static call like `OutboxMessageProcessorLog.FoundMessagesToSend(logger, messages.Length)`.

#### Logger calls to convert

There are 9 direct logger calls in `InboxMessageProcessor.cs` that need conversion:

| Line | Current call | Proposed method name |
|------|-------------|---------------------|
| 61 | `logger.LogInformation("Found {Count} inbox handler status(es) to deliver", ...)` | `FoundStatusesToDeliver(logger, count)` |
| 87-89 | `logger.LogDebug("Skipped {ConflictCount} inbox handler status(es) already claimed...", ...)` | `SkippedConflicts(logger, conflictCount)` |
| 105-106 | `logger.LogError("InboxMessage '{MessageId}' not found for handler status...", ...)` | `MessageNotFound(logger, messageId, statusId)` |
| 120-121 | `logger.LogError(ex, "Failed to deserialize properties for InboxMessage...", ...)` | `DeserializationFailed(logger, messageId, statusId, ex)` |
| 131-133 | `logger.LogWarning("Handler key '{HandlerKey}' is no longer registered...", ...)` | `HandlerKeyNotRegistered(logger, handlerKey, statusId)` |
| 169-170 | `logger.LogDebug("Inbox handler '{HandlerKey}' completed for message...", ...)` | `HandlerCompleted(logger, handlerKey, messageId)` |
| 174-176 | `logger.LogDebug("Inbox handler '{HandlerKey}' for message '{MessageId}' interrupted...", ...)` | `HandlerInterrupted(logger, handlerKey, messageId)` |
| 185-187 | `logger.LogWarning(ex, "Inbox handler '{HandlerKey}' failed for message...", ...)` | `HandlerFailed(logger, handlerKey, messageId, attempt, ex)` |
| 192-193 | `logger.LogError("Inbox handler '{HandlerKey}' for message '{MessageId}' has been poisoned...", ...)` | `HandlerPoisoned(logger, handlerKey, messageId, attempts, error)` |

#### New code to add at the bottom of `InboxMessageProcessor.cs`

```csharp
internal static partial class InboxMessageProcessorLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} inbox handler status(es) to deliver")]
    public static partial void FoundStatusesToDeliver(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped {ConflictCount} inbox handler status(es) already claimed by another worker")]
    public static partial void SkippedConflicts(ILogger logger, int conflictCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "InboxMessage '{MessageId}' not found for handler status '{StatusId}'. Poisoning status.")]
    public static partial void MessageNotFound(ILogger logger, string messageId, Guid statusId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to deserialize properties for InboxMessage '{MessageId}'. Poisoning status '{StatusId}'.")]
    public static partial void DeserializationFailed(ILogger logger, string messageId, Guid statusId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handler key '{HandlerKey}' is no longer registered. Poisoning status '{StatusId}'.")]
    public static partial void HandlerKeyNotRegistered(ILogger logger, string handlerKey, Guid statusId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Inbox handler '{HandlerKey}' completed for message '{MessageId}'")]
    public static partial void HandlerCompleted(ILogger logger, string handlerKey, string messageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Inbox handler '{HandlerKey}' for message '{MessageId}' interrupted by cancellation, will be retried via stuck detection")]
    public static partial void HandlerInterrupted(ILogger logger, string handlerKey, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox handler '{HandlerKey}' failed for message '{MessageId}', attempt {Attempt}")]
    public static partial void HandlerFailed(ILogger logger, string handlerKey, string messageId, int attempt, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Inbox handler '{HandlerKey}' for message '{MessageId}' has been poisoned after {Attempts} failed attempts. Last error: {Error}")]
    public static partial void HandlerPoisoned(ILogger logger, string handlerKey, string messageId, int attempts, string error);
}
```

Then replace each `logger.LogXxx(...)` call in the processor body with the corresponding `InboxMessageProcessorLog.Xxx(logger, ...)` call.

#### Test strategy

This is a refactoring with no behavioral change. All existing integration tests in `tests/Ratatoskr.Tests/Integration/Inbox/` exercise these log paths. Specifically:

- `InboxBasicProcessingTests` — covers the happy path (triggers `FoundStatusesToDeliver`, `HandlerCompleted`)
- `InboxErrorHandlingTests` — covers failure/poison paths (triggers `HandlerFailed`, `HandlerPoisoned`)
- `InboxCleanupServiceTests` — covers cleanup paths

Verify all inbox integration tests still pass. No new tests needed.

#### Docs to update

No docs reference the internal logging implementation. Only mark the evaluation doc:
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-LOW-8 as resolved

---

## PR 5: A-MED-9 — Expose configurable JsonSerializerOptions

**Risk:** Low | **Effort:** Simple

**Problem:** `JsonMessageSerializer` uses default `JsonSerializerOptions` with no way to configure camelCase, custom converters, or reference handling.

**Fix:** Accept `JsonSerializerOptions?` in the constructor and use it in Serialize/Deserialize calls. Wire it through registration.

**Files:**
- `src/Ratatoskr/Serializers/Json/JsonMessageSerializer.cs` — accept options
- `src/Ratatoskr/ServiceCollectionExtensions.cs` — wire options through registration
- `tests/Ratatoskr.Tests/` — test custom options
- `docs/` — document configuration
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-MED-9 as done

### Implementation Details

#### Exact code changes

**File:** `src/Ratatoskr/Serializers/Json/JsonMessageSerializer.cs` (full rewrite)

Before:
```csharp
public class JsonMessageSerializer : IMessageSerializer
{
    public string ContentType => "application/json";

    public byte[] Serialize(object message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message);
    }

    public object? Deserialize(byte[] body, Type targetType)
    {
        return JsonSerializer.Deserialize(body, targetType);
    }

    public TMessage? Deserialize<TMessage>(byte[] body)
    {
        return (TMessage?)Deserialize(body, typeof(TMessage));
    }
}
```

After:
```csharp
public class JsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions? _options;

    public JsonMessageSerializer() { }

    public JsonMessageSerializer(JsonSerializerOptions options)
    {
        _options = options;
    }

    public string ContentType => "application/json";

    public byte[] Serialize(object message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, _options);
    }

    public object? Deserialize(byte[] body, Type targetType)
    {
        return JsonSerializer.Deserialize(body, targetType, _options);
    }

    public TMessage? Deserialize<TMessage>(byte[] body)
    {
        return (TMessage?)Deserialize(body, typeof(TMessage));
    }
}
```

**File:** `src/Ratatoskr/ServiceCollectionExtensions.cs`, line 46

The current registration is:
```csharp
services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
```

Change to `TryAddSingleton` so users can register their own serializer or a pre-configured `JsonMessageSerializer` before calling `AddRatatoskr`:
```csharp
services.TryAddSingleton<IMessageSerializer, JsonMessageSerializer>();
```

Additionally, add a `ConfigureSerializer` method on `RatatoskrBuilder` or a helper extension. The simplest approach is to allow the user to register a configured `JsonMessageSerializer` directly:

```csharp
// In user code:
services.AddSingleton<IMessageSerializer>(new JsonMessageSerializer(new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
}));
services.AddRatatoskr(bus => { ... });
```

The `TryAddSingleton` change makes this work because it won't overwrite a pre-existing registration.

#### Test strategy

**File:** `tests/Ratatoskr.Tests/Core/JsonMessageSerializerTests.cs` — add new test methods.

Tests to add:

1. `Serialize_WithCamelCaseOptions_ProducesCamelCaseJson` — Create a `JsonMessageSerializer` with `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`, serialize a `TestEvent`, and assert the output uses camelCase property names.

2. `Deserialize_WithCamelCaseOptions_ReadsFromCamelCaseJson` — Serialize a camelCase JSON byte array manually, then deserialize with a camelCase-configured serializer and assert properties are populated.

3. `DefaultConstructor_UsesDefaultOptions` — Verify the parameterless constructor still works (backwards compatibility). This is already covered by existing tests but can be explicit.

These are unit tests (no containers needed), following the existing pattern in `JsonMessageSerializerTests`.

#### Docs to update

- `docs/configuration.md` — In the "Core" section, add a row to the table or a subsection documenting how to register a custom `JsonSerializerOptions`. Example:

  ```csharp
  services.AddSingleton<IMessageSerializer>(new JsonMessageSerializer(new JsonSerializerOptions
  {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
  }));
  ```

- `docs/messages-handlers.md` — If serialization is mentioned, add a note about configurable options.
- `docs/architecture.md` — If the serializer pipeline is described, mention the options parameter.
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-MED-9 as resolved

---

## PR 6: A-LOW-3 — Add metrics for cleanup operations

**Risk:** Low | **Effort:** Medium

**Problem:** `InboxCleanupService` and `OutboxCleanupService` do not record any metrics for rows deleted or batch duration.

**Fix:** Add counters/histograms to `RatatoskrDiagnostics` for cleanup rows deleted and cleanup duration. Instrument both cleanup services.

**Files:**
- `src/Ratatoskr/Core/RatatoskrDiagnostics.cs` — add cleanup metrics
- `src/Ratatoskr.EfCore/Internal/InboxCleanupService.cs` — record metrics
- `src/Ratatoskr.EfCore/Internal/OutboxCleanupService.cs` — record metrics
- `docs/` — document new metrics
- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-LOW-3 as done

### Implementation Details

#### New metrics to add

**File:** `src/Ratatoskr/Core/RatatoskrDiagnostics.cs` — add after the existing Inbox Metrics block (after line 64):

```csharp
// Cleanup Metrics
public static readonly Counter<long> OutboxCleanupCount = Meter.CreateCounter<long>(
    "ratatoskr.outbox.cleanup.count", "{message}",
    "Number of processed outbox messages deleted by cleanup.");

public static readonly Histogram<double> OutboxCleanupDuration = Meter.CreateHistogram<double>(
    "ratatoskr.outbox.cleanup.duration", "s",
    "Duration of outbox cleanup operation.",
    advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });

public static readonly Counter<long> InboxCleanupStatusCount = Meter.CreateCounter<long>(
    "ratatoskr.inbox.cleanup.status.count", "{status}",
    "Number of completed inbox handler statuses deleted by cleanup.");

public static readonly Counter<long> InboxCleanupMessageCount = Meter.CreateCounter<long>(
    "ratatoskr.inbox.cleanup.message.count", "{message}",
    "Number of orphaned inbox messages deleted by cleanup.");

public static readonly Histogram<double> InboxCleanupDuration = Meter.CreateHistogram<double>(
    "ratatoskr.inbox.cleanup.duration", "s",
    "Duration of inbox cleanup operation.",
    advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
```

#### Instrument OutboxCleanupService

**File:** `src/Ratatoskr.EfCore/Internal/OutboxCleanupService.cs`

Add `using System.Diagnostics;` at the top, then wrap the `CleanupAsync` body with a stopwatch and record after completion:

In `CleanupAsync`, line 39 (method start), add:
```csharp
var startTimestamp = Stopwatch.GetTimestamp();
```

Before the `return totalDeleted;` at line 63, add:
```csharp
RatatoskrDiagnostics.OutboxCleanupDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
if (totalDeleted > 0)
    RatatoskrDiagnostics.OutboxCleanupCount.Add(totalDeleted);
```

#### Instrument InboxCleanupService

**File:** `src/Ratatoskr.EfCore/Internal/InboxCleanupService.cs`

Add `using System.Diagnostics;` at the top, then in `CleanupAsync`:

At method start (line 39), add:
```csharp
var startTimestamp = Stopwatch.GetTimestamp();
```

Before the `return` at line 81, add:
```csharp
RatatoskrDiagnostics.InboxCleanupDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
if (totalStatusesDeleted > 0)
    RatatoskrDiagnostics.InboxCleanupStatusCount.Add(totalStatusesDeleted);
if (totalMessagesDeleted > 0)
    RatatoskrDiagnostics.InboxCleanupMessageCount.Add(totalMessagesDeleted);
```

Note: Both cleanup services need `using Ratatoskr.Core;` added since `RatatoskrDiagnostics` lives in that namespace (the core package is already referenced by `Ratatoskr.EfCore`).

#### Test strategy

**File:** `tests/Ratatoskr.Tests/Integration/Outbox/OutboxCleanupServiceTests.cs` — modify existing test `Cleanup_DeletesProcessedMessagesOlderThanRetention` (or add new) to verify the counter was incremented.

**File:** `tests/Ratatoskr.Tests/Integration/Inbox/InboxCleanupServiceTests.cs` — similarly for inbox.

To test metrics, use `System.Diagnostics.Metrics.MeterListener` to observe the `"Ratatoskr"` meter. After calling `CleanupAsync`, assert that:
- `ratatoskr.outbox.cleanup.count` recorded a value > 0
- `ratatoskr.outbox.cleanup.duration` recorded a value > 0
- `ratatoskr.inbox.cleanup.status.count` recorded a value > 0
- `ratatoskr.inbox.cleanup.message.count` recorded a value > 0
- `ratatoskr.inbox.cleanup.duration` recorded a value > 0

Add one integration test per service that exercises cleanup and verifies the metrics are emitted. Use the existing test pattern from the cleanup test classes (extend `OutboxTestBase`/`InboxTestBase`, use `FakeTimeProvider`, call `CleanupAsync` directly).

#### Docs to update

- `docs/observability.md` — Add a new "### Cleanup Metrics" section after "### Inbox Metrics" (after line 74):

  ```markdown
  ### Cleanup Metrics

  | Metric | Type | Unit | Description |
  |--------|------|------|-------------|
  | `ratatoskr.outbox.cleanup.count` | Counter | `{message}` | Processed outbox messages deleted by cleanup |
  | `ratatoskr.outbox.cleanup.duration` | Histogram | `s` | Duration of outbox cleanup operation |
  | `ratatoskr.inbox.cleanup.status.count` | Counter | `{status}` | Completed inbox handler statuses deleted by cleanup |
  | `ratatoskr.inbox.cleanup.message.count` | Counter | `{message}` | Orphaned inbox messages deleted by cleanup |
  | `ratatoskr.inbox.cleanup.duration` | Histogram | `s` | Duration of inbox cleanup operation |
  ```

- `docs/operations.md` — Add the new cleanup metrics to the "Key Metrics" table (after line 24):

  | `ratatoskr.outbox.cleanup.count` | Counter | Operational |
  | `ratatoskr.inbox.cleanup.status.count` | Counter | Operational |

- `docs/observability.md` — Add a cleanup PromQL example in the "Example Prometheus Queries" section:

  ```promql
  # Cleanup throughput
  rate(ratatoskr_outbox_cleanup_count_total[1h])
  rate(ratatoskr_inbox_cleanup_status_count_total[1h])
  ```

- `docs/brainstorms/2026-03-31-project-evaluation.md` — mark A-LOW-3 as resolved
