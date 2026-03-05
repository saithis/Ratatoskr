# Multi-DbContext Support: Channel-Scoped Handlers & Inbox

## Context

The library needs to support multiple EF Core DbContexts (bounded contexts with separate databases). Three problems exist today:

1. **Cross-DbContext durability**: Same-transaction optimization (outbox + inbox in one commit) only works when both share a DbContext. With separate databases, this is impossible.
2. **Duplicate handler invocations**: Multiple consume channels for the same message type cause ALL handlers to be invoked per channel, and the inbox creates entries for ALL handlers globally.
3. **Complex configuration**: No way to associate handlers with specific channels or DbContexts.

**Decisions made**:
- Eventual consistency for cross-DbContext messaging is acceptable
- ALL handler registration moves to consume channels (not just inbox handlers)
- `UseInbox<TDbContext>()` on a channel makes all its handlers inbox-managed by default (with opt-out)

## Target Configuration API

Handlers are registered as part of `Consumes<T>()`, making it impossible to add a handler
without consuming the message type. The stable key is the first parameter, mirroring
`AddEventConsumeChannel`. The message type generic is inferred from `Consumes<T>`.

```csharp
services.AddRatatoskr(bus =>
{
    bus.UseLocalTransport();

    // Multiple outboxes (one per publishing DbContext)
    bus.AddEfCoreOutbox<OrdersDbContext>();
    bus.AddEfCoreOutbox<ShippingDbContext>();

    // Publish channel
    bus.AddEventPublishChannel("order-events", c =>
        c.WithLocal().Produces<OrderCreated>());

    // Consume channel with inbox — all handlers default to inbox
    bus.AddEventConsumeChannel("order-events-for-orders", c =>
    {
        c.UseInbox<OrdersDbContext>();  // Sets DbContext, all handlers inbox by default
        c.Consumes<OrderCreated>(m => m
            .WithHandler<UpdateProjection>("update-projection")  // Inbox, explicit key
            .WithHandler<SendEmail>("send-email")                // Inbox, explicit key
            .WithHandler<AuditLog>("audit-log", h =>
                h.WithoutInbox()));                              // Fire-and-forget opt-out
    });

    // Another channel, different DbContext
    bus.AddEventConsumeChannel("order-events-for-shipping", c =>
    {
        c.UseInbox<ShippingDbContext>();
        c.Consumes<OrderCreated>(m => m
            .WithHandler<CreateShipment>("create-shipment"));
    });

    // Channel without inbox (all fire-and-forget, no keys needed)
    bus.AddEventConsumeChannel("order-events-metrics", c =>
    {
        c.Consumes<OrderCreated>(m => m
            .WithHandler<MetricsCollector>());
    });

    // Message without handlers is a startup error
    // bus.AddEventConsumeChannel("order-events-archive", c =>
    // {
    //     c.Consumes<OrderCreated>();  // ERROR: no handlers registered
    // });
});
```

**`WithHandler` overloads:**

```csharp
// Inbox handler (channel has UseInbox) — key required
m.WithHandler<THandler>(string stableKey)
m.WithHandler<THandler>(string stableKey, Action<HandlerBuilder> configure)

// Fire-and-forget handler (channel without UseInbox, or explicit opt-out) — key optional
m.WithHandler<THandler>()
m.WithHandler<THandler>(Action<HandlerBuilder> configure)
```

**Rules:**
- Handlers are always nested under `Consumes<T>()` — impossible to add a handler for an unconsumed message type
- `c.UseInbox<TDbContext>()` must be called on a channel for any handler to use inbox
- With `UseInbox`: handlers are inbox-managed by default, stable key is required (opt-out with `WithoutInbox()`)
- Without `UseInbox`: all handlers are fire-and-forget, key is optional
- `Consumes<T>()` without handlers is a startup error
- Each handler is invoked exactly once (scoped to its channel)

## Implementation Plan

### Step 1: Channel-scoped handler registration

**Move handler registration from bus to message consumption.**

- Add `MessageConsumptionBuilder<TMessage>` — returned by `Consumes<T>(Action<MessageConsumptionBuilder<T>>)`
  - `WithHandler<THandler>(string stableKey)` — inbox handler with key
  - `WithHandler<THandler>(string stableKey, Action<HandlerBuilder> configure)` — inbox handler with config
  - `WithHandler<THandler>()` — fire-and-forget handler (no key)
  - `WithHandler<THandler>(Action<HandlerBuilder> configure)` — fire-and-forget with config
  - Each `WithHandler` call registers the handler in DI and stores a `ChannelHandlerRegistration`
- Pass `IServiceCollection` from `RatatoskrBuilder` to `ConsumeChannelBuilder` → `MessageConsumptionBuilder`
- Remove parameterless `Consumes<T>()` overload (every consumed message must have at least one handler)
- Remove `RatatoskrBuilder.AddHandler<>()` and `PendingHandlers`
- Handler registrations are stored on the `MessageRegistration` (extending the existing type), keeping the handler-to-message-type relationship tight

**New types**:
- `MessageConsumptionBuilder<TMessage>` — fluent builder for handlers within a `Consumes<T>()` call
- `ChannelHandlerRegistration` — record `(Type MessageType, Type HandlerType, bool IsInbox, string? InboxKey)`

**Files:**
- `src/Ratatoskr/Config/ConsumeChannelBuilder.cs` — new `Consumes<T>(Action<MessageConsumptionBuilder<T>>)` overload
- New: `src/Ratatoskr/Config/MessageConsumptionBuilder.cs`
- `src/Ratatoskr/RatatoskrBuilder.cs` — remove `AddHandler<>()`, `PendingHandlers`, pass `Services` to channel builders
- `src/Ratatoskr/Core/ChannelRegistration.cs` — handler list on `MessageRegistration` or as channel extension
- New: `ChannelHandlerRegistration` record

### Step 2: ChannelHandlerRegistry

**Replace DI-based handler discovery with explicit registry.**

New `ChannelHandlerRegistry` built from channel registrations at startup:
- `GetFireAndForgetHandlers(channelName, messageType)` → handler types for dispatcher
- `GetInboxHandlers(channelName)` → inbox handler registrations for acceptor
- `GetInboxHandlersByDbContext(dbContextType, messageType)` → for outbox same-tx optimization
- `IsInboxManaged(handlerType, messageType)` → for backward compat if needed
- `GetInboxRegistrationByKey(key)` → for inbox processor

**Files:**
- New: `src/Ratatoskr/Core/ChannelHandlerRegistry.cs`

### Step 3: Update MessageDispatcher

**Use ChannelHandlerRegistry instead of DI discovery + filter.**

- Remove `IHandlerFilter` dependency
- Query `channelHandlerRegistry.GetFireAndForgetHandlers(channelName, messageType)` for handler types
- Delete `src/Ratatoskr/Core/IHandlerFilter.cs`

**Files:**
- `src/Ratatoskr/Core/MessageDispatcher.cs` — use registry instead of DI discovery
- `src/Ratatoskr/Core/IHandlerFilter.cs` — **delete**

### Step 4: Add channelName to IMessageRouteInterceptor

`MessageRouter.RouteAsync()` already has `channelName`. Pass it to the interceptor.

```csharp
public interface IMessageRouteInterceptor
{
    Task<RouteInterceptResult> BeforeDispatchAsync(
        byte[] body, MessageProperties properties, string transportName,
        string channelName, CancellationToken cancellationToken);
}
```

Also change `MessageRouter` to accept `IEnumerable<IMessageRouteInterceptor>` (for multiple DbContexts).

**Files:**
- `src/Ratatoskr/Core/IMessageRouteInterceptor.cs` — add `channelName`
- `src/Ratatoskr/Core/MessageRouter.cs` — pass `channelName`, accept `IEnumerable<>`

### Step 5: Channel-level inbox registration (EF Core package)

**Rewrite `UseEfCoreInbox` to work at the channel level.**

- `c.UseInbox<TDbContext>()` on `ConsumeChannelBuilder` stores `ChannelInboxConfig` (DbContext type) as channel extension
- Deferred action iterates all channels, finds inbox configs, populates `InboxHandlerRegistry` with channel + DbContext info
- Each unique DbContext type gets its own `InboxProcessor<TDbContext>`, `InboxMessageProcessor<TDbContext>`, `InboxAcceptor<TDbContext>`
- Per-DbContext options via `InboxOptionsHolder<TDbContext>` wrapper

**Files:**
- `src/Ratatoskr.EfCore/InboxPublicApiExtensions.cs` — major rewrite, channel-level API
- `src/Ratatoskr.EfCore/Internal/InboxHandlerRegistration.cs` — add `ChannelName`, `DbContextType`
- `src/Ratatoskr.EfCore/Internal/InboxHandlerRegistry.cs` — add channel-based + DbContext-based indexes
- `src/Ratatoskr.EfCore/Internal/InboxHandlerFilter.cs` — **delete**
- New: `InboxOptionsHolder<TDbContext>` wrapper
- `src/Ratatoskr.EfCore/Internal/InboxProcessor.cs` — use `InboxOptionsHolder<TDbContext>`
- `src/Ratatoskr.EfCore/Internal/InboxMessageProcessor.cs` — use `InboxOptionsHolder<TDbContext>`

### Step 6: Channel-scoped InboxAcceptor

- `AcceptAsync()` gains `channelName` parameter
- Looks up handlers via `channelHandlerRegistry.GetInboxHandlers(channelName)` instead of global wire-type lookup
- Only creates `InboxHandlerStatusEntity` for that channel's handlers

**Files:**
- `src/Ratatoskr.EfCore/Internal/InboxAcceptor.cs` — add `channelName`, scoped lookup
- `src/Ratatoskr.EfCore/Internal/InboxRouteInterceptor.cs` — pass `channelName`

### Step 7: OutboxTriggerInterceptor per-DbContext filtering

When creating same-transaction inbox entries for local transport:

1. Find all consume channels for message type via `ChannelRegistry.FindConsumeChannelsForType()`
2. For each channel with inbox config where `DbContextType == typeof(TDbContext)`:
   - Create `InboxMessageEntity` + `InboxHandlerStatusEntity` for that channel's inbox handlers
3. Skip channels with inbox on other DbContexts (handled async by transport)

**Files:**
- `src/Ratatoskr.EfCore/Internal/OutboxTriggerInterceptor.cs` — filter by DbContext, iterate channels

### Step 8: Multiple outbox support

Allow `AddEfCoreOutbox<TDbContext>()` to be called multiple times with different DbContext types. Each gets its own interceptor, processor, and options.

Per-DbContext options via `OutboxOptionsHolder<TDbContext>` wrapper (same pattern as inbox).

**Files:**
- `src/Ratatoskr.EfCore/PublicApiExtensions.cs` — per-DbContext options
- `src/Ratatoskr.EfCore/Internal/OutboxProcessor.cs` — use options holder
- `src/Ratatoskr.EfCore/Internal/OutboxMessageProcessor.cs` — use options holder

### Step 9: Startup validation

- `Consumes<T>()` without any `WithHandler` calls → error
- Handler with `WithInbox()` on a channel without `UseInbox<>()` → error
- Inbox handler without a stable key (channel has `UseInbox` but handler registered without key) → error
- Duplicate handler keys across all channels → error
- Channel with `UseInbox<>()` but no matching `AddEfCoreOutbox<>()` for same-tx optimization → warning (not error, still works via transport)

**Files:**
- `src/Ratatoskr.EfCore/InboxConfigurationValidator.cs` — rewrite for channel-scoped model

### Step 10: Update tests

All existing inbox/outbox integration tests need updating for the new API. New tests:
- Multiple DbContexts with separate inboxes
- Same message type consumed by handlers on different DbContexts
- Same-transaction optimization only fires for matching DbContext
- Channel-scoped handler invocation (no duplicate invocations)
- Startup validation errors

**Files:**
- `tests/Ratatoskr.Tests/Integration/InboxTests.cs`
- `tests/Ratatoskr.Tests/Integration/OutboxTests.cs`
- All other integration tests using `AddHandler<>`

### Step 11: Update documentation

- `docs/architecture.md` — channel-scoped handler model
- `docs/inbox.md` — new configuration examples
- `docs/configuration.md` — handler registration on channels

## Verification

1. `dotnet build` — all projects compile
2. `dotnet test` — all tests pass (updated + new)
3. Integration test: publish from `OrdersDbContext` outbox, verify same-tx inbox entries for `OrdersDbContext` handlers, async delivery for `ShippingDbContext` handlers
4. Integration test: no duplicate handler invocations across channels
5. Docs are consistent with implementation
