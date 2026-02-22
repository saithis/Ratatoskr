# Testing

Ratatoskr provides a comprehensive testing toolkit that supports three tiers of testing, from simple unit tests to full integration tests with parallel safety.

## Packages

| Package | Purpose |
|---|---|
| `Ratatoskr` (Testing namespace) | Core test infrastructure: `FakeRatatoskr`, `TestSession`, `MessageSink`, `HandlerTestInvoker` |
| `Ratatoskr.Testing` | ASP.NET Core integration: `WebApplicationFactory` extensions, `WebTestSession`, HTTP session propagation |
| `Ratatoskr.EfCore` (Testing namespace) | Outbox test helpers: `OutboxTestHelper`, `OutboxStagingAssertions` |

## Tier 1: Unit Testing Handlers

Test handlers in isolation using `HandlerTestInvoker`. No DI container needed.

```csharp
var handler = new OrderCreatedHandler(mockRepo.Object);
await HandlerTestInvoker.InvokeAsync(handler, new OrderCreated { OrderId = "123" });
mockRepo.Verify(r => r.SaveAsync(It.IsAny<Order>()), Times.Once);
```

## Tier 2: Unit Testing Message Sending

### FakeRatatoskr

A simple test double for `IRatatoskr` that stores typed messages without serialization.

```csharp
var ratatoskr = new FakeRatatoskr();
var sut = new OrderService(ratatoskr);

await sut.PlaceOrderAsync(new PlaceOrderCommand { ProductId = "abc" });

// Typed assertion - returns the matched message
var msg = ratatoskr.ShouldHavePublished<OrderCreated>(m => m.ProductId == "abc");
msg.ProductId.Should().Be("abc");

// Other assertions
ratatoskr.ShouldHavePublishedCount(1);
ratatoskr.ShouldNotHavePublished<OrderCancelled>();
ratatoskr.ShouldBeEmpty(); // fails if any messages
```

### Outbox Staging Assertions

Verify messages were staged in the outbox without processing them.

```csharp
dbContext.OutboxMessages.Add(new OrderCreated { OrderId = "123" });
await dbContext.SaveChangesAsync();

dbContext.OutboxMessages.ShouldHaveStaged<OrderCreated>(m => m.OrderId == "123");
dbContext.OutboxMessages.ShouldHaveStagedCount(1);
```

## Tier 3: Integration Testing

### AddTestRatatoskr (without WebApplicationFactory)

Set up a test container with the test transport directly.

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddTestRatatoskr(bus =>
{
    bus.AddEventPublishChannel("events", c => c.Produces<OrderCreated>());
    bus.AddEventConsumeChannel("events-in", c => c.Consumes<OrderCreated>());
    bus.AddHandler<OrderCreated, OrderCreatedHandler>();
});

await using var provider = services.BuildServiceProvider();
var harness = provider.GetRequiredService<RatatoskrTestHarness>();
var bus = provider.GetRequiredService<IRatatoskr>();

// Publish and assert
await bus.PublishDirectAsync(new OrderCreated { OrderId = "123" });
harness.Sent.ShouldContain<OrderCreated>(m => m.OrderId == "123");

// Simulate receiving a message (dispatches to handlers)
await harness.SimulateReceiveAsync(new OrderCreated { OrderId = "456" });
```

### WebApplicationFactory Integration

Replace the real transport with the test transport in an ASP.NET Core test host.

```csharp
var factory = new WebApplicationFactory<Program>()
    .WithRatatoskrTestServices();

var harness = factory.GetTestHarness();
var client = factory.CreateClient();

await client.PostAsJsonAsync("/api/orders", new { ProductId = "abc" });
harness.Sent.ShouldContain<OrderCreated>();
```

### Parallel-Safe Testing with Sessions

For tests that share a `WebApplicationFactory`, use sessions to isolate message tracking.

```csharp
var factory = new WebApplicationFactory<Program>()
    .WithRatatoskrTestServices();

// Each test creates its own session
await using var session = factory.CreateTestSession();
var client = session.CreateHttpClient(); // injects session header

await client.PostAsJsonAsync("/api/orders", new { ProductId = "abc" });

// Only sees messages from THIS session
session.Sent.ShouldContain<OrderCreated>(m => m.ProductId == "abc");
```

Sessions work by:
1. `CreateHttpClient()` injects an `X-Ratatoskr-Session` HTTP header
2. `TestSessionMiddleware` reads the header and sets an `AsyncLocal` session context
3. `TestSessionEnricher` copies the session ID into message properties
4. `MessageSinkView` filters messages by session ID

### Message Routing (In-Process E2E)

Enable `RouteMessages` to dispatch published messages to handlers in-process, without a real broker.

```csharp
var factory = new WebApplicationFactory<Program>()
    .WithRatatoskrTestServices(o => o.RouteMessages = true);

await using var session = factory.CreateTestSession();

// Simulate an incoming message (dispatched to handlers)
await session.SimulateReceiveAsync(new OrderCreated { OrderId = "123" });

// Handler processed it and sent a follow-up
session.Sent.ShouldContain<NotificationSent>(m => m.OrderId == "123");
```

### Real Transport (TestContainers)

Keep the real transport for full end-to-end testing with a real broker.

```csharp
var factory = new WebApplicationFactory<Program>()
    .WithRatatoskrTestServices(o => o.ReplaceTransport = false);

await using var session = factory.CreateTestSession();
var client = session.CreateHttpClient();

await client.PostAsJsonAsync("/api/orders", new { ProductId = "abc" });

// Message went through real broker
var sent = await session.Sent.WaitForAsync<OrderCreated>(
    m => m.ProductId == "abc",
    timeout: TimeSpan.FromSeconds(10));
```

### Outbox Testing

Process outbox messages synchronously in tests.

```csharp
var factory = new WebApplicationFactory<Program>()
    .WithRatatoskrTestServices();

await using var session = factory.CreateTestSession();
var client = session.CreateHttpClient();

await client.PostAsJsonAsync("/api/orders", new { ProductId = "abc" });

// Process outbox explicitly
using var scope = session.CreateScope();
await OutboxTestHelper.ProcessAllAsync<AppDbContext>(scope.ServiceProvider);

session.Sent.ShouldContain<OrderCreated>();
```

## Assertion API

Both `MessageSink` and `MessageSinkView` (session-scoped) support the same assertion extensions:

| Method | Description |
|---|---|
| `ShouldContain<T>(predicate?)` | Assert a message of type T exists, returns `SentMessage<T>` |
| `ShouldNotContain<T>()` | Assert no messages of type T |
| `ShouldBeEmpty()` | Assert no messages at all |
| `ShouldHaveCount(n)` | Assert total message count |
| `ShouldHaveCount<T>(n)` | Assert count of messages of type T |
| `GetMessages<T>()` | Get all messages of type T |
| `WaitForAsync<T>(predicate?, timeout?)` | Wait for a message to appear |

## TestTransportOptions

| Option | Default | Description |
|---|---|---|
| `ReplaceTransport` | `true` | Replace real transport with test transport |
| `RouteMessages` | `false` | Dispatch published messages to handlers in-process |
