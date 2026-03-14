---
_layout: landing
---

# Ratatoskr

Ratatoskr is a .NET messaging library for reliable event-driven applications. It provides transport-agnostic message publishing and consumption with durable delivery guarantees through the Outbox and Inbox patterns, built on the [CloudEvents](https://cloudevents.io/) specification.

## Features

- **Channel-first design** — Declare channels with intent (event publish, command consume) and attach message types, handlers, and transport configuration
- **Reliable delivery** — Outbox pattern ensures messages are never lost, even if the broker is temporarily unavailable
- **Durable inbox** — Per-handler deduplication and retry with persistent state via EF Core
- **CloudEvents native** — Messages are [CloudEvents](https://cloudevents.io/) by default with W3C trace context propagation
- **Multiple transports** — RabbitMQ for production messaging, EF Core for broker-free scenarios
- **Horizontally scalable** — Distributed locks and optimistic concurrency for safe multi-instance deployment
- **Observable** — Built-in OpenTelemetry tracing and metrics
- **Testable** — `Ratatoskr.Testing` package with trace-isolated parallel test sessions

## Packages

| Package | Description |
|---------|-------------|
| `Ratatoskr` | Core abstractions, message routing, CloudEvents support, and serialization |
| `Ratatoskr.EfCore` | Outbox and Inbox durability patterns, EF Core transport |
| `Ratatoskr.RabbitMq` | RabbitMQ transport with topology management and retry/DLQ support |
| `Ratatoskr.Testing` | Test utilities with W3C trace-isolated sessions and assertion helpers |

## Quick Example

Define a message:

[!code-csharp[](../examples/Docs/Messages/OrderPlaced.cs#OrderPlaced)]

Handle it:

[!code-csharp[](../examples/Docs/Handlers/OrderPlacedHandler.cs#OrderPlacedHandler)]

Configure channels and register handlers:

[!code-csharp[](../examples/Docs/Program.cs#AddRatatoskr)]

Publish directly or through the outbox:

[!code-csharp[](../examples/Docs/Program.cs#PublishDirectExample)]

[!code-csharp[](../examples/Docs/Program.cs#PublishOutboxExample)]

## Key Terminology

| Term | Definition |
|------|------------|
| **Channel** | A named messaging endpoint that groups related message types. Channels have intent: event publish, event consume, command publish, or command consume. |
| **Transport** | The delivery mechanism that moves messages between services. Ratatoskr supports RabbitMQ and EF Core (database) transports. |
| **Handler** | A class implementing `IMessageHandler<T>` that processes a specific message type. Handlers are registered on consume channels. |
| **Handler key** | A stable string identifier for an inbox-managed handler (e.g., `"fulfillment"`). Persisted in the database — must not change across deployments. |
| **Fire-and-forget** | A handler registered without a key. Invoked immediately during message dispatch — no persistence, no retry. |
| **Inbox-managed** | A handler registered with a key on a channel with `UseInbox()`. Persisted to the database and delivered with retry, deduplication, and isolation. |
| **Poisoned message** | A message or handler status that has exhausted its retry budget. Remains in the database for manual investigation. |
| **Route interceptor** | An `IMessageRouteInterceptor` implementation called by the `MessageRouter` before dispatch. Used by the inbox to accept messages into the database. |

## When to Use Ratatoskr

Ratatoskr is a good fit when you need:

- Reliable message delivery with transactional outbox guarantees
- Per-handler deduplication and retry via the inbox pattern
- CloudEvents-based messaging with standard metadata
- A channel-first API that enforces ownership and topology conventions

Ratatoskr is **not** designed for:

- Request/reply or RPC patterns
- In-memory pub/sub without persistence requirements
- Saga or process manager orchestration (use MassTransit or Wolverine)
- Stream processing (use Kafka, EventStoreDB, or similar)

## What's Next

- [Getting Started](getting-started.md) — Build your first Ratatoskr application step by step
- [Architecture](architecture.md) — Understand how messages flow through the system
- [Configuration Reference](configuration.md) — All configuration options at a glance
