# Channels & Routing

Ratatoskr uses a channel-first design where channels are the primary unit of configuration. A channel groups related message types, assigns transport settings, and declares ownership intent. This page covers the channel API, ownership rules, and message routing.

## Channel-First Design

Everything in Ratatoskr starts with a channel. You don't configure messages or handlers in isolation — you attach them to a channel that declares:

1. **Intent** — Are you publishing or consuming? Events or commands?
2. **Transport** — How are messages delivered? (RabbitMQ, EF Core)
3. **Message types** — What messages flow through this channel?
4. **Handlers** — Who processes incoming messages? (consume channels only)

This design centralizes topology decisions and enforces ownership conventions at the API level.

## Channel Types

Ratatoskr provides four channel methods, each encoding a specific intent:

| Method | Intent | You Are | Topology Action (RabbitMQ) |
|--------|--------|---------|---------------------------|
| `AddEventPublishChannel` | Publish events you own | Producer | **Declare** exchange |
| `AddCommandPublishChannel` | Send commands to others | Sender | **Validate** exchange exists |
| `AddEventConsumeChannel` | Subscribe to others' events | Subscriber | **Validate** exchange, **declare** queue, **bind** |
| `AddCommandConsumeChannel` | Process commands you own | Processor | **Declare** exchange + queue |

### Ownership Rules

The key insight is **who owns what**:

- **Event producers** own their exchange. They declare it because they control the schema and topology.
- **Command consumers** own their exchange. They declare it because they define the contract for incoming commands.
- **Event subscribers** and **command senders** only validate that the exchange exists. They don't create it.

This prevents topology conflicts when multiple services share infrastructure.

## Configuration

### Publish Channels

Publish channels declare what messages you produce:

[!code-csharp[](../examples/Docs/Program.cs#ConfigurePublishChannels)]

Each `Produces<T>()` call registers a message type on the channel. The message's `[RatatoskrMessage]` type is used as the default routing key.

### Consume Channels

Consume channels declare what messages you handle:

[!code-csharp[](../examples/Docs/Program.cs#ConfigureConsumeChannels)]

Each `Consumes<T>()` call registers a message type and its handlers. You can register multiple handlers per message type and multiple message types per channel.

## Routing

### Publishing

When you call `IRatatoskr.PublishDirectAsync<T>(message)`, Ratatoskr:

1. Looks up all publish channels that have `Produces<T>()` registered
2. For each channel, sends the message to the configured transport
3. Uses the message's `[RatatoskrMessage]` type as the routing key (unless overridden)

### Consuming

When a message arrives from a transport:

1. The `MessageRouter` matches the message type to consume channels
2. If the channel has `UseInbox()`, the `InboxAcceptor` persists the message and handler statuses
3. Fire-and-forget handlers are invoked immediately by the `MessageDispatcher`
4. Inbox-managed handlers are delivered later by the `InboxProcessor`

### Route Interceptors

The `IMessageRouteInterceptor` interface allows extending the routing pipeline. The inbox uses this to intercept messages before dispatch:

```csharp
public interface IMessageRouteInterceptor
{
    Task<InterceptResult> InterceptAsync(
        byte[] body,
        MessageProperties properties,
        CancellationToken cancellationToken);
}
```

The inbox registers a `CompositeInboxRouteInterceptor` that routes messages to the correct `InboxAcceptor<T>` based on the channel configuration. You generally don't need to implement this interface directly.

## Routing Key Override

By default, the routing key is the `[RatatoskrMessage]` type string. You can override it per message type:

```csharp
bus.AddEventPublishChannel("orders.events", c => c
    .WithRabbitMq(r => r.WithTopicExchange())
    .Produces<OrderPlaced>()
    .Produces<OrderCancelled>(cfg => cfg.WithRoutingKey("order.cancelled.v2")));
```

## Message Type Resolution

On the consume side, the `ChannelRegistry` maps CloudEvents `type` strings to .NET types. When a message arrives with `type: "order.placed"`, the dispatcher looks up which `Consumes<T>()` registrations match and resolves the corresponding CLR type for deserialization.

## Startup Validation

Ratatoskr validates channel configuration at startup and fails fast on:

- Inbox channels with handlers missing stable keys
- Duplicate handler keys across channels
- Missing transport configuration on channels

This catches configuration errors before the application starts processing messages.

## What's Next

- [CloudEvents](cloudevents.md) — CloudEvents metadata and content modes
- [RabbitMQ](rabbitmq.md) — Exchange types, topology, and transport-specific configuration
- [EF Core Transport](efcore-transport.md) — Database-based message delivery
