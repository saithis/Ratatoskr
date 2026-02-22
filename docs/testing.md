# Testing

Ratatoskr provides a dedicated testing package `Ratatoskr.Testing` that makes it easy to write high-confidence integration tests with full pipeline observability, parallel test isolation, and ergonomic assertion APIs.

## Setup

Install the `Ratatoskr.Testing` package and register it in your test setup:

```csharp
services.AddRatatoskrTesting();
```

This registers a `MessageTracker` singleton that observes all message activities flowing through the pipeline. In production (when not registered), there is zero overhead — the observer collection is simply empty.

## Pipeline Stages

The tracker captures messages at every stage of the pipeline:

| Stage | Description |
|:---|:---|
| `Published` | After `IRatatoskr.PublishDirectAsync` completes |
| `Sent` | After bytes are sent to the transport (e.g. RabbitMQ) |
| `Received` | When the consumer receives a message from the transport |
| `Dispatched` | After handler invocation completes (includes result and exception) |
| `OutboxStaged` | When a message is serialized into an outbox entity during `SaveChanges` |
| `OutboxSent` | When the outbox processor sends a message to the transport |

## Session-Based API

The primary API creates a `MessageTrackingSession` per test. Each session generates a unique W3C trace ID that correlates all messages published within its scope — enabling **parallel test isolation**.

```csharp
[Test]
public async Task OrderCreated_HandlerProcessesSuccessfully()
{
    // ... setup with AddRatatoskrTesting() ...

    await using var session = Services.CreateTrackingSession();

    // Publish within the session's trace context
    await InScopeAsync(async ctx =>
    {
        var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
        await bus.PublishDirectAsync(new OrderCreatedEvent { OrderId = "123" });
    });

    // Wait for the handler to complete
    await session.WaitForDispatched<OrderCreatedEvent>();

    // Assert on the dispatched message
    var dispatched = session.Dispatched.Single<OrderCreatedEvent>();
    dispatched.GetMessage<OrderCreatedEvent>().OrderId.Should().Be("123");
    dispatched.Result.Should().Be(DispatchResult.Success);
}
```

### Creating a Session

```csharp
// From IServiceProvider
await using var session = services.CreateTrackingSession();

// From IHost
await using var session = host.CreateTrackingSession();

// With custom default timeout
await using var session = services.CreateTrackingSession(
    defaultTimeout: TimeSpan.FromSeconds(30));
```

### Waiting for Messages

Each wait method blocks until a matching message arrives or the timeout expires:

```csharp
await session.WaitForPublished<OrderCreatedEvent>();
await session.WaitForSent<OrderCreatedEvent>();
await session.WaitForReceived<OrderCreatedEvent>();
await session.WaitForDispatched<OrderCreatedEvent>();

// With custom timeout
await session.WaitForDispatched<OrderCreatedEvent>(TimeSpan.FromSeconds(15));

// With additional predicate
await session.WaitForDispatched<OrderCreatedEvent>(
    predicate: m => m.GetMessage<OrderCreatedEvent>().OrderId == "123");
```

### Querying Messages

Each stage has a `MessageCollection` property for querying captured messages:

```csharp
// Get single message of type (throws if 0 or >1)
var msg = session.Dispatched.Single<OrderCreatedEvent>();

// Get first message of type
var msg = session.Dispatched.First<OrderCreatedEvent>();

// Get all messages of type
IReadOnlyList<TrackedMessage> all = session.Dispatched.All<OrderCreatedEvent>();

// Count
int count = session.Published.Count;
```

### Assertion Helpers

```csharp
// Assert at least one message of type exists (returns it)
var msg = session.Dispatched.ShouldHaveMessage<OrderCreatedEvent>();

// Assert no messages of type exist
session.Published.ShouldHaveNoMessage<OrderCancelledEvent>();
```

### TrackedMessage Properties

Each `TrackedMessage` provides rich access to the captured data:

```csharp
var msg = session.Dispatched.Single<OrderCreatedEvent>();

// Typed access to the deserialized message
OrderCreatedEvent event = msg.GetMessage<OrderCreatedEvent>();

// Message properties (CloudEvents metadata)
msg.Properties.Type    // "order.created"
msg.Properties.Id      // message ID
msg.Properties.Source  // source URI

// Dispatch result (only at Dispatched stage)
msg.Result             // DispatchResult.Success, RecoverableError, etc.

// Exception (if handler threw)
msg.Exception          // null on success

// Raw serialized bytes (exact wire format at Sent stage)
byte[] raw = msg.RawBody;

// Pipeline stage
msg.Stage              // MessageStage.Dispatched

// Trace ID for correlation
msg.TraceId            // W3C trace ID string
```

## Action-Based API

For simpler scenarios, the action-based API wraps session creation and waiting into a single call:

```csharp
[Test]
public async Task CreateOrder_PublishesEvent()
{
    // ... setup ...

    var session = await Services
        .TrackActivity()
        .Timeout(TimeSpan.FromSeconds(10))
        .WaitForMessage<OrderCreatedEvent>(MessageStage.Dispatched)
        .ExecuteAndWaitAsync(async () =>
        {
            using var scope = Services.CreateScope();
            var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new OrderCreatedEvent { OrderId = "123" });
        });

    // Assert
    session.Dispatched.Single<OrderCreatedEvent>()
        .Result.Should().Be(DispatchResult.Success);

    await session.DisposeAsync();
}
```

### PublishAndWaitAsync

A convenience method that resolves `IRatatoskr` and publishes for you:

```csharp
var session = await Services
    .TrackActivity()
    .PublishAndWaitAsync(new OrderCreatedEvent { OrderId = "123" });

session.Dispatched.ShouldHaveMessage<OrderCreatedEvent>();
```

## Transport Shape Assertions

The `RawBody` property on `TrackedMessage` gives you the exact bytes sent on the wire. This is useful for verifying the CloudEvents envelope format:

```csharp
await using var session = Services.CreateTrackingSession();

await InScopeAsync(async ctx =>
{
    var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
    await bus.PublishDirectAsync(new OrderCreatedEvent { OrderId = "123" });
});

await session.WaitForSent<OrderCreatedEvent>();

var sent = session.Sent.Single<OrderCreatedEvent>();
var rawJson = Encoding.UTF8.GetString(sent.RawBody!);

// Assert on the wire format
rawJson.Should().Contain("123");
sent.Properties.Type.Should().Be("order.created");
sent.Properties.Id.Should().NotBeNullOrEmpty();
```

## Outbox Testing

The tracker captures outbox-specific stages, making it easy to verify the full outbox flow:

```csharp
await using var session = Services.CreateTrackingSession();

await InScopeAsync(async ctx =>
{
    var dbContext = ctx.ServiceProvider.GetRequiredService<MyDbContext>();
    dbContext.OutboxMessages.Add(
        new OrderCreatedEvent { OrderId = "123" },
        new MessageProperties().SetExchange("orders"));
    await dbContext.SaveChangesAsync();
});

await session.WaitForDispatched<OrderCreatedEvent>(TimeSpan.FromSeconds(10));

// Verify it went through the outbox
session.OutboxStaged.ShouldHaveMessage<OrderCreatedEvent>();
session.OutboxSent.ShouldHaveMessage<OrderCreatedEvent>();

// Verify it was handled
session.Dispatched.Single<OrderCreatedEvent>()
    .Result.Should().Be(DispatchResult.Success);
```

## Parallel Test Isolation

Each `MessageTrackingSession` creates a unique W3C trace ID. Messages published within a session inherit this trace ID through `Activity.Current`. The consumer propagates the trace ID via message headers. Sessions filter messages by matching trace ID, so **parallel tests never interfere with each other**.

```csharp
// Two sessions running concurrently see only their own messages
await using var session1 = Services.CreateTrackingSession();
// ... publish message A in session1's context ...

await using var session2 = Services.CreateTrackingSession();
// ... publish message B in session2's context ...

session1.Dispatched.Single<MyEvent>().GetMessage<MyEvent>().Id.Should().Be("A");
session2.Dispatched.Single<MyEvent>().GetMessage<MyEvent>().Id.Should().Be("B");

// Trace IDs are different
session1.TraceId.Should().NotBe(session2.TraceId);
```

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Core Library (Ratatoskr)                                   │
│                                                             │
│  IMessageActivityObserver interface                         │
│  Hooks in: Ratatoskr, MessageDispatcher,                    │
│    RabbitMqMessageSender, RabbitMqConsumer,                  │
│    OutboxTriggerInterceptor, OutboxMessageProcessor          │
└──────────────────────────┬──────────────────────────────────┘
                           │ implements
┌──────────────────────────▼──────────────────────────────────┐
│  Ratatoskr.Testing                                          │
│                                                             │
│  MessageTracker (singleton, collects all activities)         │
│  MessageTrackingSession (per-test, trace-ID-scoped view)    │
│  TrackedMessage (rich model per captured message)            │
│  MessageCollection (queryable + assertion helpers)           │
│  ActivityTracker (action-based convenience API)              │
└─────────────────────────────────────────────────────────────┘
```
