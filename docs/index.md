---
_layout: landing
---

# Ratatoskr

A modern .NET library for reliable event publishing using the **Outbox/Inbox pattern** with [CloudEvents](https://cloudevents.io/) support.

## Features

- **Reliable delivery** — Outbox pattern ensures messages are never lost, even if the broker is temporarily unavailable
- **Durable inbox** — Per-handler deduplication and retry with persistent state via EF Core
- **CloudEvents** — Messages are sent as [CloudEvents](https://cloudevents.io/) by default; alternative formats are supported
- **RabbitMQ transport** — Production-ready RabbitMQ implementation with AsyncAPI bindings
- **Horizontally scalable** — Works correctly with multiple application instances
- **Observability** — Built-in support for metrics and tracing
- **Easy to test** — `Ratatoskr.Testing` package with zero-overhead test utilities

## Packages

| Package | Description |
|---------|-------------|
| `Ratatoskr` | Core abstractions and CloudEvents support |
| `Ratatoskr.EfCore` | Outbox and Inbox pattern via Entity Framework Core |
| `Ratatoskr.RabbitMq` | RabbitMQ transport implementation |
| `Ratatoskr.Testing` | Test utilities and helpers |

## Quick Start

Install the packages:

```bash
dotnet add package Ratatoskr
dotnet add package Ratatoskr.EfCore
dotnet add package Ratatoskr.RabbitMq
```

Register in your application:

```csharp
builder.Services.AddRatatoskr(b =>
{
    b.UseRabbitMq(o => o.ConnectionString = "amqp://guest:guest@localhost/");
    b.UseEfCore<AppDbContext>();
});
```

## Documentation

- [Architecture](architecture.md) — How Ratatoskr works under the hood
- [Configuration](configuration.md) — All configuration options
- [Inbox](inbox.md) — Durable per-handler message consumption
- [Operations](operations.md) — Running in production
- [Testing](testing.md) — Testing your application with Ratatoskr
- [Topology](topology.md) — RabbitMQ exchange and queue topology
- [API Reference](api/index.md) — Full API documentation
