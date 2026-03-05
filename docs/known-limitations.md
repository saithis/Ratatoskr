# Known Limitations

## Cross-DbContext outbox→inbox loses atomicity

When the outbox and inbox use different DbContext types (e.g. outbox on `OrdersDbContext`, inbox on `PaymentsDbContext`), the `OutboxTriggerInterceptor` cannot write inbox entries in the same transaction as the outbox entry. Instead, the consumer-side `InboxAcceptor` writes inbox entries in a separate transaction after the local transport delivers the message. This is still safe (the outbox guarantees delivery), but involves two separate transactions instead of one — inbox acceptance is eventual, not crash-safe in the same way as the single-DbContext path.

## SkipDispatch design: inbox is all-or-nothing per message type

When `UseInbox()` is enabled for a message type, **all** handlers for that message type go through the inbox. There is no way to have a mix of inbox-managed and fire-and-forget handlers for the same message type on the same channel. If you need both durable and fire-and-forget processing for the same event, use separate message types.

## Outbox `SourceContext` column and legacy rows

The `OutboxMessageEntity` includes a `SourceContext` column (the full type name of the DbContext that created the message) to scope cleanup operations per DbContext. This column has `HasDefaultValue("")` so that existing rows from before the column was added get an empty string. Legacy rows with empty `SourceContext` are cleaned up by **any** DbContext's cleanup processor, which matches the previous behavior where cleanup was not scoped.
