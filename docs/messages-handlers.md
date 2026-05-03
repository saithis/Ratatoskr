# Messages & Handlers

Messages are the data contracts that flow through Ratatoskr. Handlers are the classes that process them. This page covers how to define messages, register handlers, access message metadata, and customize serialization.

## Defining Messages

A message is any .NET class or record decorated with `[RatatoskrMessage]`. The attribute sets the CloudEvents `type` used for routing:

[!code-csharp[](../examples/Docs/Messages/OrderPlaced.cs#OrderPlaced)]

The type string (e.g., `"order.placed"`) is used as:

- The CloudEvents `type` attribute
- The default RabbitMQ routing key
- The key for resolving handlers in the `ChannelRegistry`

> [!TIP]
> Use dot-separated, lowercase type names following a `{domain}.{event}` or `{domain}.{action}` convention. This aligns with CloudEvents best practices and maps naturally to RabbitMQ topic routing keys.

### Events vs. Commands

Ratatoskr distinguishes between events and commands at the channel level, not the message level. The same message class can be published on an event channel or a command channel. The distinction affects topology ownership:

- **Events** describe something that happened. The producer owns the exchange.
- **Commands** request an action. The consumer owns the exchange.

See [Channels & Routing](channels-routing.md) for details on ownership rules.

## Implementing Handlers

Handlers implement `IMessageHandler<T>` and are resolved from the DI container:

[!code-csharp[](../examples/Docs/Handlers/ProcessPaymentHandler.cs#ProcessPaymentHandler)]

Each handler receives:

- **`message`** — The deserialized message object
- **`properties`** — CloudEvents metadata (ID, source, type, timestamp, trace context, and extension attributes)
- **`cancellationToken`** — Triggered on application shutdown or handler timeout (if configured)

## Registering Handlers

Handlers are registered inside the `Consumes<T>()` builder on a consume channel. There are two registration modes:

### Fire-and-Forget

```csharp
bus.AddEventConsumeChannel("orders.events", c => c
    .WithRabbitMq(r => r.WithQueueName("orders.events.handler"))
    .Consumes<OrderPlaced>(m => m
        .WithHandler<OrderPlacedHandler>()));
```

The handler is invoked immediately during message dispatch. No database persistence, no retry. If the handler throws, the transport's retry mechanism (e.g., RabbitMQ requeue/DLQ) handles the failure.

### Inbox-Managed

```csharp
bus.AddEventConsumeChannel("orders.events", c => c
    .WithRabbitMq(r => r.WithQueueName("orders.events.handler"))
    .Consumes<OrderPlaced>(m => m
        .WithHandler<OrderPlacedHandler>("order-handler"))
    .UseInbox<OrderDbContext>());
```

The handler is registered with a **stable key** (`"order-handler"`). The message and handler status are persisted to the database before the transport acknowledges receipt. The `InboxProcessor` delivers the message with automatic retry and deduplication.

> [!WARNING]
> Handler keys are persisted in the database. Renaming a key causes existing in-flight messages to be poisoned with an "unknown handler key" error. Choose stable, descriptive keys.

### Registration Rules

| Scenario | Allowed? |
|----------|----------|
| Fire-and-forget handler on a channel **without** `UseInbox()` | Yes |
| Inbox-managed handler (with key) on a channel **with** `UseInbox()` | Yes |
| Handler without key on a channel **with** `UseInbox()` | No — throws `InvalidOperationException` at startup |
| Multiple handlers for the same message type | Yes — each handler runs independently |

> [!NOTE]
> Handler keys must be **globally unique** across all channels and DbContexts.

### Fan-out (Rabbit, no inbox)

You can register **multiple** `.WithHandler<THandler>()` handlers for the same message type on one consume channel. Each handler runs for every delivery.

On RabbitMQ **without** `UseInbox()`, use **fire-and-forget** registration (parameterless `WithHandler<THandler>()`). Stable-key overloads (`WithHandler<THandler>("key")`) register **inbox-managed** handlers; they are only dispatched when the channel has `UseInbox<TDbContext>()` (and `AddEfCoreDurability` runs inbox validation at startup).

If one handler throws, the transport typically **nacks the whole message** and both handlers run again on redelivery — there is no per-handler isolation without an inbox.

**Runnable demo:** `examples/PlaygroundHost/Program.cs` registers two handlers for `OrderPlaced` on the same queue. The server scenario `fanout-two-handlers-on-orderplaced` exercises this. See [examples/README.md](../examples/README.md#feature-coverage-where).

## MessageProperties

The `MessageProperties` object provides access to CloudEvents metadata and extension attributes:

```csharp
public Task HandleAsync(OrderPlaced message, MessageProperties properties,
    CancellationToken cancellationToken)
{
    var messageId = properties.Id;           // CloudEvents id
    var source = properties.Source;          // CloudEvents source
    var type = properties.Type;             // CloudEvents type (e.g., "order.placed")
    var time = properties.Time;             // CloudEvents time
    var traceParent = properties.TraceParent; // W3C traceparent header

    // Extension attributes
    var custom = properties.Extensions["my-attribute"];

    return Task.CompletedTask;
}
```

See [CloudEvents](cloudevents.md) for the full list of standard and extension attributes.

## Serialization

Ratatoskr uses `System.Text.Json` by default.

### Configuring JsonSerializerOptions

To customize the built-in JSON serializer (e.g. camelCase naming, custom converters), use `ConfigureJsonSerialization`:

```csharp
builder.Services.AddRatatoskr(bus =>
{
    bus.ConfigureJsonSerialization(json =>
    {
        json.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });
});
```

The default serializer uses `System.Text.Json` defaults (PascalCase, case-sensitive).

### Custom Serializer

For full control, implement `IMessageSerializer` directly:

```csharp
public class CustomSerializer : IMessageSerializer
{
    public string ContentType => "application/custom";

    public byte[] Serialize(object message)
    {
        // Custom serialization logic
    }

    public object? Deserialize(byte[] data, Type type)
    {
        // Custom deserialization logic
    }

    public TMessage? Deserialize<TMessage>(byte[] body)
    {
        // Custom generic deserialization logic
    }
}
```

Register it in the DI container **before** calling `AddRatatoskr`:

```csharp
builder.Services.AddSingleton<IMessageSerializer, CustomSerializer>();
```

### Per-Message Serializer

You can override the serializer for a specific message type by configuring the message registration:

```csharp
builder.Services.AddSingleton<ProtobufOrderSerializer>();

builder.Services.AddRatatoskr(bus =>
{
    bus.AddEventPublishChannel("orders.events", c => c
        .Produces<OrderPlaced>(m => m.WithSerializer<ProtobufOrderSerializer>()));

    bus.AddEventConsumeChannel("orders.events", c => c
        .Consumes<OrderPlaced>(
            h => h.WithHandler<OrderPlacedHandler>(),
            m => m.WithSerializer<ProtobufOrderSerializer>()));
});
```

Behavior and rules:

- If a message has no `.WithSerializer<TSerializer>()`, Ratatoskr uses the global `IMessageSerializer`.
- The configured serializer must be registered in DI.
- A single message type can only use one serializer across channels.
- `MessageProperties.ContentType` is set from the selected serializer for each message.

### Schema Evolution

When evolving message schemas:

- **Adding fields** is safe — old messages deserialize with `default` values for new fields
- **Removing fields** is safe — the old field is silently ignored during deserialization
- **Renaming fields** breaks compatibility — the old value is lost

For breaking changes, introduce a new message type and migrate consumers before producers.

## DI Scoping

Handler lifetime depends on the registration mode:

- **Fire-and-forget:** Each handler invocation creates a new DI scope. The scope is disposed after the handler completes.
- **Inbox-managed:** Each handler invocation creates its own isolated DI scope. A failure or `ChangeTracker.Clear()` in one handler's scope does not affect other handlers for the same message.

This means injected services like `DbContext` are scoped per handler invocation — you get a fresh instance each time.

## What's Next

- [Channels & Routing](channels-routing.md) — Channel-first design, ownership, and routing rules
- [CloudEvents](cloudevents.md) — CloudEvents metadata and extension attributes
- [Inbox](inbox.md) — Deep dive into inbox-managed handler behavior
