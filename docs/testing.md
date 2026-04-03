# Testing

The `Ratatoskr.Testing` package provides pipeline observability, parallel test isolation via W3C trace IDs, and ergonomic assertion APIs. It has zero overhead in production — the observer collection is empty when not registered.

## Setup

Install the package and register it in your test setup:

```bash
dotnet add package Ratatoskr.Testing
```

```csharp
services.AddRatatoskrTesting();
```

This registers a `MessageTracker` singleton that observes all message activities flowing through the pipeline.

## Session-Based API

The primary API creates a `MessageTrackingSession` per test. Each session generates a unique W3C trace ID that correlates all messages published within its scope — enabling **parallel test isolation**.

```csharp
[Test]
public async Task OrderPlaced_HandlerProcessesSuccessfully()
{
    await using var session = Services.CreateTrackingSession();

    await InScopeAsync(async ctx =>
    {
        var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
        await bus.PublishDirectAsync(new OrderPlaced(Guid.NewGuid(), "test@example.com", 99.99m));
    });

    var dispatched = await session.WaitForDispatched<OrderPlaced>();
    dispatched.GetMessage<OrderPlaced>().CustomerEmail.Should().Be("test@example.com");
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
var published = await session.WaitForPublished<OrderPlaced>();
var sent = await session.WaitForSent<OrderPlaced>();
var received = await session.WaitForReceived<OrderPlaced>();
var dispatched = await session.WaitForDispatched<OrderPlaced>();

// With custom timeout
var dispatched = await session.WaitForDispatched<OrderPlaced>(
    TimeSpan.FromSeconds(15));

// With predicate
var dispatched = await session.WaitForDispatched<OrderPlaced>(
    predicate: m => m.GetMessage<OrderPlaced>().OrderId == orderId);
```

> [!TIP]
> Prefer using the return value from `WaitFor*` directly over querying the collection afterward — this avoids race conditions where a later stage completes before an earlier stage's observer notification is recorded.

## Pipeline Stages

The tracker captures messages at every stage:

| Stage | Description |
|-------|-------------|
| `Published` | After each send attempt during `PublishDirectAsync` (includes `TransportName` and `Exception`) |
| `Sent` | After bytes are sent to the transport |
| `Received` | When the consumer receives a message from the transport |
| `Dispatched` | After handler invocation completes (includes result and exception) |
| `OutboxStaged` | When a message is serialized into an outbox entity during `SaveChanges` |
| `OutboxSent` | When the outbox processor sends a message to the transport |
| `InboxQueued` | When a message is accepted into the inbox |
| `InboxDispatched` | When an inbox handler delivery is attempted (success or failure) |
| `InboxPoisoned` | When an inbox handler status exceeds max retries |

## Querying Messages

Each stage has a `MessageCollection` for querying:

```csharp
var msg = session.Dispatched.Single<OrderPlaced>();
var msg = session.Dispatched.First<OrderPlaced>();
IReadOnlyList<TrackedMessage> all = session.Dispatched.All<OrderPlaced>();
int count = session.Published.Count;
```

### Assertion Helpers

```csharp
var msg = session.Dispatched.ShouldHaveMessage<OrderPlaced>();
session.Published.ShouldHaveNoMessage<OrderCancelled>();
```

### TrackedMessage Properties

```csharp
var msg = session.Dispatched.Single<OrderPlaced>();

OrderPlaced order = msg.GetMessage<OrderPlaced>();  // Deserialized message
msg.Properties.Type   // "order.placed"
msg.Properties.Id     // CloudEvents message ID
msg.Result            // DispatchResult.Success (Dispatched stage only)
msg.Exception         // null on success
byte[] raw = msg.RawBody;  // Serialized bytes
msg.Stage             // MessageStage.Dispatched
msg.TraceId           // W3C trace ID
```

## Action-Based API

For simpler scenarios, the action-based API wraps session creation and waiting:

```csharp
[Test]
public async Task CreateOrder_PublishesEvent()
{
    await using var session = await Services
        .TrackActivity()
        .Timeout(TimeSpan.FromSeconds(10))
        .WaitForMessage<OrderPlaced>(MessageStage.Dispatched)
        .ExecuteAndWaitAsync(async () =>
        {
            using var scope = Services.CreateScope();
            var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new OrderPlaced(Guid.NewGuid(), "test@example.com", 99.99m));
        });

    session.Dispatched.Single<OrderPlaced>()
        .Result.Should().Be(DispatchResult.Success);
}
```

### PublishAndWaitAsync

A convenience method that resolves `IRatatoskr` and publishes for you (always waits for `Dispatched`):

```csharp
await using var session = await Services
    .TrackActivity()
    .PublishAndWaitAsync(new OrderPlaced(Guid.NewGuid(), "test@example.com", 99.99m));

session.Dispatched.ShouldHaveMessage<OrderPlaced>();
```

> [!NOTE]
> `PublishAndWaitAsync` does not support custom `WaitForMessage` conditions. Use `ExecuteAndWaitAsync` when you need custom wait conditions.

## Transport Wire Format Assertions

The `TransportMessage` property captures the transport-level wire representation at the **Sent** and **Received** stages:

```csharp
var sent = await session.WaitForSent<OrderPlaced>();
var transport = sent.TransportMessage!;

// AMQP properties
transport.Headers["content-type"].Should().Be("application/json");
transport.Headers["type"].Should().Be("order.placed");

// CloudEvents AMQP headers (binary mode)
transport.Headers["cloudEvents_specversion"].Should().Be("1.0");
transport.Headers["cloudEvents_type"].Should().Be("order.placed");

// Raw wire body
var wireBody = Encoding.UTF8.GetString(transport.Body);
wireBody.Should().Contain("test@example.com");

// Routing metadata
transport.Metadata["exchange"].Should().Be("orders.events");
transport.Metadata["routing-key"].Should().Be("order.placed");
```

## Outbox Testing

Verify the full outbox flow:

```csharp
await using var session = Services.CreateTrackingSession();

await InScopeAsync(async ctx =>
{
    var dbContext = ctx.ServiceProvider.GetRequiredService<OrderDbContext>();
    dbContext.OutboxMessages.Add(new OrderPlaced(Guid.NewGuid(), "test@example.com", 99.99m));
    await dbContext.SaveChangesAsync();
});

var dispatched = await session.WaitForDispatched<OrderPlaced>(
    TimeSpan.FromSeconds(10));
dispatched.Result.Should().Be(DispatchResult.Success);

session.OutboxStaged.ShouldHaveMessage<OrderPlaced>();
```

## Inbox Testing

Use `WithoutBackgroundProcessing()` for deterministic inbox testing:

```csharp
services.AddRatatoskr(bus =>
{
    bus.AddEfCoreDurability<OrderDbContext>(d =>
        d.UseInbox(inbox => inbox.WithoutBackgroundProcessing()));

    // ... channel configuration ...
});

// In the test body, trigger processing manually (use true to match production / recover stuck rows):
var processor = Services.GetRequiredService<InboxMessageProcessor<OrderDbContext>>();
await processor.ProcessBatchAsync(
    includeStuckMessageDetection: true,
    CancellationToken.None);
```

This gives you full control over when inbox processing runs, so you can inspect database state between acceptance and delivery. With `includeStuckMessageDetection: false`, only rows that have never been claimed (`ProcessingStartedAt == null`) are eligible; this avoids concurrent processors picking the same row while another worker is sending, but it also skips stuck recovery until you pass `true` or advance time.

> [!NOTE]
> `WithoutBackgroundProcessing()` also disables the cleanup service.

## Parallel Test Isolation

Each session creates a unique W3C trace ID. Messages published within a session inherit this trace ID through `Activity.Current`. Sessions filter by matching trace ID, so **parallel tests never interfere with each other**:

```csharp
await using var session1 = Services.CreateTrackingSession();
// ... publish message A in session1's context ...

await using var session2 = Services.CreateTrackingSession();
// ... publish message B in session2's context ...

session1.Dispatched.Single<OrderPlaced>().GetMessage<OrderPlaced>().OrderId.Should().Be(idA);
session2.Dispatched.Single<OrderPlaced>().GetMessage<OrderPlaced>().OrderId.Should().Be(idB);
```

## What's Next

- [Observability](observability.md) — OpenTelemetry tracing and metrics setup
- [Inbox](inbox.md) — Understanding inbox processing for test scenarios
- [Outbox](outbox.md) — Understanding outbox processing for test scenarios
