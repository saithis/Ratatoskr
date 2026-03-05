# Code Review: `multi-db-context-support`

## Summary

This PR tries to do three things at once -- multi-DbContext support, a new per-message inbox activation model, and automatic cleanup processors.

---

## 1. The "Known Limitation" Is a Design Bug You Shipped Anyway

`docs/known-limitations.md` openly admits that the same message type on multiple channels with different DbContexts causes handlers to be duplicated across all acceptors.

The `InboxHandlerRegistry` maps by wire type name **globally**, not per channel. So when the same message type is consumed on two channels with different DbContexts, both `InboxAcceptor<Context1>` and `InboxAcceptor<Context2>` create handler statuses for ALL handlers. That's not a "known limitation" -- that's an incomplete feature with a workaround ("use distinct message types") that defeats the purpose of the feature.

---

## 2. Singleton Factory Side-Effect: Trigger Registration at Resolution Time

The `InboxProcessor<T>` singleton factory in `RegisterPerDbContextServices` still mutates `InboxDbContextRegistry` via `RegisterTrigger()` as a **side effect of DI resolution order**. If something resolves `InboxDbContextRegistry` before the `InboxProcessor<T>` singleton is created, `GetTrigger()` returns null.

The `OutboxTriggerInterceptor` does exactly this -- it silently swallows the null, so the inbox processor never gets triggered. Whether this works depends on the order services happen to be resolved. That's fragile. Move trigger registration to configuration time (e.g. inside the deferred action / `InboxConfigurationFinalizer`).

---

## 3. `SkipDispatch` Removes Mixed Handler Support -- a Regression

The old design allowed inbox handlers and fire-and-forget handlers on the **same message type**. The new design makes this impossible -- `SkipDispatch = true` bypasses the dispatcher entirely. The docs say "use separate message types" but that's a breaking change for existing users who had mixed setups. The `Inbox_ChangeTrackerClear_DoesNotAffectNonInboxHandler` test was deleted rather than adapted.

---

## Edge Cases Missing

1. **Cleanup race with active processing**: cleanup can cascade-delete a message while the processor holds a reference to its handler status.
2. **`ExecuteDeleteAsync` and cascade deletes**: cascade behavior varies by DB provider; in-memory/SQLite may leave orphaned rows.
3. **Multiple `IMessageRouteInterceptor` registrations**: last-registered wins, silently dropping the inbox interceptor.
4. **Same handler type for inbox + non-inbox message types**: key requirement may bleed into non-inbox scenarios.
5. **Channel name uniqueness**: duplicate channel names silently overwrite in `InboxRoutingTable`.

---

## Tests Missing

1. Duplicate handler registration -- `AddHandlerCore` throws for duplicate handler+message pairs, no test.
2. Same message type, two channels, different DbContexts -- the known limitation should have a test documenting the actual behavior.
3. Cleanup under load / race conditions -- no test that processes messages while cleanup runs concurrently.
4. `WithoutBackgroundProcessing` + cleanup interaction -- if background processing is disabled but cleanup is enabled, the cleanup processor still runs as a hosted service. Is that intended? No test.
5. Inbox cleanup telemetry -- no test verifying `ratatoskr.inbox.cleanup.count` metric is emitted correctly.

---

## Simplification Opportunities

1. **Move trigger registration to configuration time** instead of as a singleton factory side-effect. Register the trigger eagerly in the deferred action / `InboxConfigurationFinalizer` since you have the DbContext type available at registration time.
