# Known Limitations

## Same message type on multiple channels with different DbContexts

When the same message type (e.g. `TestEvent`) has `UseInbox()` and is consumed on two channels with different `UseInbox<TDbContext>()` mappings, all handlers for that message type are registered globally — not per-channel. Both `InboxAcceptor<Context1>` and `InboxAcceptor<Context2>` will create handler statuses for ALL handlers of the message type, even if some handlers are "intended" for a specific context.

Workaround: use distinct message types per channel when different DbContexts are involved.

## Outbox `SourceContext` column and legacy rows

The `OutboxMessageEntity` includes a `SourceContext` column (the full type name of the DbContext that created the message) to scope cleanup operations per DbContext. This column has `HasDefaultValue("")` so that existing rows from before the column was added get an empty string. Legacy rows with empty `SourceContext` are cleaned up by **any** DbContext's cleanup processor, which matches the previous behavior where cleanup was not scoped.
