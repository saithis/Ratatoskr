# Plan: Address Review Comments & Solve Known Limitations

## Context

Three AI reviewers (Antigravity, Claude, Cursor) provided feedback on the `multi-db-context-support` branch. The main issues fall into three categories: a design limitation in InboxHandlerRegistry that causes silent bugs, a fragile trigger registration pattern, and missing test coverage for multi-DbContext scenarios.

---

## 1. Startup validation: same message type on different DbContexts

**Problem:** `InboxHandlerRegistry` is global — maps wire type name to handlers without per-channel/per-DbContext scoping. When the same message type (e.g. `test.event`) is consumed on two channels with different DbContexts, both `InboxAcceptor<Context1>` and `InboxAcceptor<Context2>` create handler statuses for ALL handlers of that wire type.

**Fix:** Add startup validation in `InboxConfigurationFinalizer.PopulateRoutingTable()` that throws if the same wire type name appears on channels mapped to different DbContexts. This prevents the bug at startup rather than silently producing wrong behavior.

**Files:**
- `src/Ratatoskr.EfCore/Internal/InboxConfigurationFinalizer.cs` — Add validation in `PopulateRoutingTable()`
- `docs/known-limitations.md` — Update the limitation to note it's now caught at startup
- `tests/Ratatoskr.Tests/Integration/InboxTests.cs` — Add test: `Inbox_SameMessageTypeDifferentDbContexts_ThrowsAtStartup`

**Implementation:**
```csharp
// In PopulateRoutingTable(), after the foreach loop over channels:
// Track wireTypeName -> dbContextType mapping, throw on conflict
var wireTypeToDbContext = new Dictionary<string, (Type DbContextType, string ChannelName)>();

// Inside the message loop, when UseInbox is true:
if (wireTypeToDbContext.TryGetValue(wireTypeName, out var existing)
    && existing.DbContextType != dbContextType)
{
    throw new InvalidOperationException(
        $"Message type '{wireTypeName}' is inbox-managed on channel '{channel.ChannelName}' " +
        $"(DbContext: {dbContextType.Name}) and channel '{existing.ChannelName}' " +
        $"(DbContext: {existing.DbContextType.Name}). Inbox-managed message types must use " +
        $"the same DbContext across all channels. Use distinct message types per DbContext.");
}
wireTypeToDbContext[wireTypeName] = (dbContextType, channel.ChannelName);
```

---

## 2. Fix trigger registration side-effect

**Problem:** `InboxProcessor<TDbContext>` registers its trigger in `InboxDbContextRegistry` as a side-effect of singleton factory resolution. If `InboxDbContextRegistry.GetTrigger()` is called before the processor is resolved, it returns null. `OutboxTriggerInterceptor` silently swallows the null, so the inbox processor never gets triggered.

**Fix:** Replace the singleton factory side-effect with eager trigger registration using a `Lazy<>` pattern or factory delegate in `InboxDbContextRegistry`.

**Files:**
- `src/Ratatoskr.EfCore/Internal/InboxDbContextRegistry.cs` — Change trigger storage from `IProcessorTrigger` to `Func<IProcessorTrigger>`
- `src/Ratatoskr.EfCore/InboxPublicApiExtensions.cs` — Register trigger factory at config time instead of DI resolution time
- `src/Ratatoskr.EfCore/Internal/OutboxTriggerInterceptor.cs` — Update `GetTrigger` call

**Implementation:**
In `InboxDbContextRegistry`, change trigger registration to use a factory:
```csharp
private readonly Dictionary<Type, Func<IProcessorTrigger>> _triggerFactories = new();

public void RegisterTriggerFactory(Type dbContextType, Func<IProcessorTrigger> factory)
    => _triggerFactories[dbContextType] = factory;

public IProcessorTrigger? GetTrigger(Type dbContextType)
    => _triggerFactories.TryGetValue(dbContextType, out var factory) ? factory() : null;
```

In `RegisterPerDbContextServices`, remove the side-effect from the factory and register a trigger factory:
```csharp
// Register processor as plain singleton (no side-effect)
builder.Services.AddSingleton<InboxProcessor<TDbContext>>();

// Register trigger factory at config time — lazy resolution ensures processor exists
state.DbContextRegistry.RegisterTriggerFactory(typeof(TDbContext),
    () => /* resolved lazily from IServiceProvider at runtime */);
```

The factory delegate will capture the IServiceProvider and lazily resolve the processor on first trigger call.

---

## 3. Fix zero-handler orphan in cleanup

**Problem:** If an `InboxMessageEntity` has 0 handler statuses (e.g. manual DB intervention, future bug), the completed cleanup query evaluates `!Any(pending) && !Any(poisoned)` = `true`, silently deleting orphaned messages.

**Fix:** Add a condition requiring at least one handler status to exist for the "completed" cleanup path.

**Files:**
- `src/Ratatoskr.EfCore/Internal/InboxCleanupProcessor.cs` — Add `Any(s => s.MessageId == m.Id)` to completed cleanup query
- `tests/Ratatoskr.Tests/Integration/InboxTests.cs` — Add test: `InboxCleanup_OrphanedMessageWithNoHandlers_IsNotDeleted`

---

## 4. Restore removed test

**Problem:** `Inbox_MissingUseEfCoreInbox_ThrowsAtStartup` was removed in unstaged changes. The validation logic in `InboxConfigurationFinalizer:92-99` still exists and should be tested.

**Fix:** Restore the test from the staged version.

**Files:**
- `tests/Ratatoskr.Tests/Integration/InboxTests.cs` — Restore `Inbox_MissingUseEfCoreInbox_ThrowsAtStartup`

---

## 5. Rename misleading test

**Problem:** `Inbox_TwoChannelsDifferentDbContexts_EachUsesItsOwnDatabase` — both DbContexts use the same PostgreSQL database. The name claims database isolation but the test only proves routing isolation.

**Fix:** Rename to `Inbox_TwoChannelsDifferentDbContexts_EachRoutedToCorrectDbContext`.

**Files:**
- `tests/Ratatoskr.Tests/Integration/InboxTests.cs`

---

## 6. Add missing tests

### 6a. Cross-DbContext outbox->inbox via local transport
The path in `OutboxTriggerInterceptor:76` where `inboxDbContextType != typeof(TDbContext)` has zero test coverage. Add test proving the consumer-side `InboxAcceptor` handles inbox acceptance when outbox and inbox use different DbContexts.

### 6b. Multiple channels pointing to same DbContext
Add test: two consume channels both using `UseInbox<TestDbContext>()`, verify both channels' messages are processed and cleaned up correctly.

### 6c. Outbox cleanup with multiple DbContexts on shared database
Inbox cleanup already has `InboxCleanup_SharedDatabase_DifferentRetention_OnlyDeletesOwnChannelMessages`. Add equivalent for outbox: `OutboxCleanup_SharedDatabase_DifferentRetention_OnlyDeletesOwnSourceContextMessages`.

**Files:**
- `tests/Ratatoskr.Tests/Integration/InboxTests.cs` — Tests 6a, 6b
- `tests/Ratatoskr.Tests/Integration/OutboxTests.cs` — Test 6c

---

## 7. Update documentation

**Files:**
- `docs/known-limitations.md` — Update first limitation to note it's validated at startup. Add new entries for:
  - Cross-DbContext outbox->inbox loses atomicity (consumer-side acceptance is eventual, not crash-safe)
  - SkipDispatch design: when UseInbox() is enabled for a message type, all handlers go through inbox (no mixed inbox + fire-and-forget on the same message type)
- `docs/inbox.md` — Update if relevant sections exist

---

## Items NOT addressed (and why)

| Review Comment | Reason |
|---|---|
| ChannelName leaking into InboxMessageEntity (Antigravity) | By design — required for per-channel cleanup scoping |
| Named Options instead of TypedOptionsRegistry (Antigravity) | Current approach is simpler and type-safe; Named Options adds string-key indirection with no benefit |
| Simplify InboxConfigurationFinalizer (Antigravity) | Already clean; the deferred pattern is necessary |
| High-volume batching test (Antigravity) | Batching already works; 100K row test is expensive and low ROI |
| Provider validation for ExecuteDeleteAsync (Antigravity/Cursor) | PostgreSQL is the primary target; provider-specific translation issues are EF Core's responsibility |
| SkipDispatch mixed handler regression (Cursor) | Intentional design: UseInbox() is all-or-nothing per message type. Document instead of reverting. |
| Cleanup race with active processing (Cursor) | Cascade delete is atomic at DB level; processor handles missing statuses gracefully via `GetByKey` returning null |
| Multiple IMessageRouteInterceptor (Cursor) | Already solved by CompositeInboxRouteInterceptor design |
| Channel name uniqueness (Cursor) | Channels are registered in ChannelRegistry which is upstream; inbox just maps what exists |

---

## Verification

1. Run full test suite: `cd tests/Ratatoskr.Tests && dotnet run`
2. Verify new startup validation test throws with correct message
3. Verify zero-handler orphan test passes (message not deleted)
4. Verify cross-DbContext outbox->inbox test works end-to-end
5. Verify all existing tests still pass (no regressions)
