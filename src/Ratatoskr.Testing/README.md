# Ratatoskr.Testing

Testing utilities for [Ratatoskr](https://www.nuget.org/packages/Ratatoskr), providing message tracking, session-based test isolation, and assertion helpers for integration tests.

## Features

- **Message Tracking**: Observe all messages flowing through the pipeline at every stage (Published, Sent, Received, Dispatched, Outbox).
- **Parallel Test Isolation**: Each test session gets a unique W3C trace ID, so parallel tests never interfere with each other.
- **Assertion Helpers**: Ergonomic APIs to query and assert on tracked messages.
- **Wire Format Assertions**: Verify transport-level details like AMQP headers and raw wire body.

## Getting Started

Install the package via NuGet:

```bash
dotnet add package Ratatoskr.Testing
```

Register testing services in your test setup:

```csharp
services.AddRatatoskrTesting();
```

## Usage

```csharp
await using var session = Services.CreateTrackingSession();

await InScopeAsync(async ctx =>
{
    var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
    await bus.PublishDirectAsync(new OrderCreatedEvent { OrderId = "123" });
});

var dispatched = await session.WaitForDispatched<OrderCreatedEvent>();
dispatched.GetMessage<OrderCreatedEvent>().OrderId.Should().Be("123");
dispatched.Result.Should().Be(DispatchResult.Success);
```

For full documentation, see the [testing guide](https://github.com/saithis/Ratatoskr/blob/main/docs/testing.md).
