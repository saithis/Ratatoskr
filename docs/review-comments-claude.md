# Code Review: Multi-DbContext Support — Round 2

Items from my initial review that were **fully addressed** have been removed. What remains is either unaddressed or has new observations from the fix.

---

## STILL OPEN: Cross-DbContext outbox->inbox local transport path is untested

`OutboxTriggerInterceptor` skips inbox entry creation when `inboxDbContextType != typeof(TDbContext)` and relies on the consumer-side `InboxAcceptor` to handle it. **This path still has zero test coverage.** The only outbox+inbox test (`Inbox_OutboxToLocalTransport_EndToEndCrashSafe`) uses the same DbContext for both.

When outbox and inbox use different DbContexts, crash-safety is lost — the outbox commits but inbox acceptance isn't atomic. This should either be tested and documented as a known limitation, or the test should prove the fallback path works.

**File:** `src/Ratatoskr.EfCore/Internal/OutboxTriggerInterceptor.cs:76`

---

## STILL OPEN: Misleading test name

`Inbox_TwoChannelsDifferentDbContexts_EachUsesItsOwnDatabase` — both DbContexts still point to `PostgresConnectionString` (the same database). The name claims database isolation but the test only proves routing isolation by channel name. Rename to something like `EachUsesItsOwnDbContext` or `RoutedToCorrectDbContext`.

**File:** `tests/Ratatoskr.Tests/Integration/InboxTests.cs`

---

## STILL OPEN: Global InboxHandlerRegistry with multi-DbContext

Already documented in `docs/known-limitations.md`, but there's still no startup validation to catch the problematic case. If someone accidentally consumes the same message type on two channels with different DbContexts, they get silently wrong behavior (duplicate handler statuses) instead of a clear startup error.

Consider adding validation in `InboxConfigurationFinalizer` to throw if the same wire type name appears on channels mapped to different DbContexts.

**File:** `src/Ratatoskr.EfCore/Internal/InboxHandlerRegistry.cs`

---

## WIP: `Inbox_MissingUseEfCoreInbox_ThrowsAtStartup` test removed in unstaged changes

The staged diff adds this test validating that `UseInbox<T>()` without `UseEfCoreInbox<T>()` throws. The unstaged diff removes it. The validation logic in `InboxConfigurationFinalizer:92-99` still exists. Is the test failing, or was this intentional?

---

## REMAINING MISSING TESTS

1. **Cross-DbContext outbox->inbox via local transport** — outbox on DbContext A, inbox on DbContext B, end-to-end
2. **Same message type on two channels with different DbContexts** — documents actual behavior of the known limitation
3. **Multiple channels pointing to same DbContext** — channel-a and channel-b both use `UseInbox<TestDbContext>()`
4. **Outbox cleanup with multiple DbContexts on shared database** — inbox cleanup has `InboxCleanup_SharedDatabase_DifferentRetention_OnlyDeletesOwnChannelMessages` but outbox has no equivalent multi-DbContext cleanup test
