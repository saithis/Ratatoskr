# RabbitMQ Transport

The RabbitMQ transport provides production-grade message delivery via AMQP. It handles exchange and queue topology provisioning, consumer lifecycle management, CloudEvents AMQP encoding, retry with dead-letter queues, and health checks.

## Setup

Install the package and configure the transport:

```bash
dotnet add package Ratatoskr.RabbitMq
```

[!code-csharp[](../examples/Docs/Program.cs#ConfigureRabbitMq)]

The `ConnectionString` accepts a standard AMQP URI (`amqp://user:pass@host:port/vhost`).

## Exchange Types

Ratatoskr supports three AMQP exchange types:

| Exchange Type | Routing Behavior | Use Case |
|---------------|-----------------|----------|
| **Topic** (default) | Pattern matching on routing key (`order.*`, `#`) | Events with flexible subscriptions |
| **Direct** | Exact routing key match | Commands targeted at specific consumers |
| **Fanout** | Broadcast to all bound queues (routing key ignored) | Notifications to all subscribers |

Configure per channel:

```csharp
// Topic exchange (default) — pattern-based routing
bus.AddEventPublishChannel("orders.events", c => c
    .WithRabbitMq(r => r.WithTopicExchange())
    .Produces<OrderPlaced>());

// Direct exchange — exact routing key match
bus.AddCommandConsumeChannel("orders.commands", c => c
    .WithRabbitMq(r => r
        .WithDirectExchange()
        .WithQueueName("orders.commands.queue"))
    .Consumes<ProcessPayment>(m => m.WithHandler<ProcessPaymentHandler>()));

// Fanout exchange — broadcast to all queues
bus.AddEventPublishChannel("notifications", c => c
    .WithRabbitMq(r => r.WithFanoutExchange())
    .Produces<SystemAlert>());
```

## Queue Types

| Queue Type | Description |
|------------|-------------|
| **Quorum** (default) | Replicated across cluster nodes. Recommended for production. |
| **Classic** | Single-node queue. Lower latency, no replication. |

```csharp
.WithRabbitMq(r => r
    .WithQueueName("orders.handler")
    .WithQueueType(QueueType.Classic))
```

> [!TIP]
> Quorum queues are the default and recommended choice. They survive node failures and provide better data safety. Use classic queues only when you need specific features not supported by quorum queues (e.g., exclusive queues, auto-delete).

## Topology Management

On startup, `RabbitMqTopologyManager` automatically provisions the required AMQP topology based on your channel configuration:

| Channel Type | Topology Action |
|-------------|-----------------|
| Event publish | **Declare** exchange |
| Command publish | **Validate** exchange exists (passive declare) |
| Event consume | **Validate** exchange, **declare** queue, **bind** queue to exchange |
| Command consume | **Declare** exchange, **declare** queue, **bind** queue to exchange |

When managed retry is enabled (default), the topology manager also creates retry and dead-letter queues.

## Retry and Dead-Letter Queues

Failed messages are automatically routed through a retry queue with a configurable delay, then back to the main queue. After exhausting retries, messages land in the dead-letter queue (DLQ).

```mermaid
flowchart LR
    Exchange["Exchange"] --> MainQueue["Main Queue<br/>orders.handler"]
    MainQueue -->|"Success"| Ack["Acknowledged"]
    MainQueue -->|"Failure"| RetryQueue["Retry Queue<br/>orders.handler.retry<br/>(TTL: 30s)"]
    RetryQueue -->|"TTL expired"| MainQueue
    MainQueue -->|"Max retries exceeded"| DLQ["Dead Letter Queue<br/>orders.handler.dlq"]
```

Configure retry behavior:

```csharp
.WithRabbitMq(r => r
    .WithQueueName("orders.handler")
    .WithRetry(maxRetries: 5, delay: TimeSpan.FromSeconds(60)))
```

Or use the builder callback for full control:

```csharp
.WithRabbitMq(r => r
    .WithQueueName("orders.handler")
    .WithRetry(retry => retry
        .WithMaxRetries(5)
        .WithDelay(TimeSpan.FromMinutes(1))
        .WithDeadLetterSuffix(".dead")
        .WithRetrySuffix(".wait")))
```

### Retry Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `MaxRetries` | `3` | Maximum retry attempts before routing to DLQ |
| `Delay` | `30 seconds` | TTL on the retry queue (delay between retries) |
| `UseManaged` | `true` | Whether Ratatoskr provisions retry/DLQ topology automatically |
| `DeadLetterSuffix` | `".dlq"` | Suffix appended to queue name for the DLQ |
| `RetrySuffix` | `".retry"` | Suffix appended to queue name for the retry queue |

Set `WithManaged(false)` if you manage retry topology externally (e.g., via Terraform or RabbitMQ policies).

## Consumer Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `QueueName` | (required) | Name of the queue to consume from |
| `PrefetchCount` | `10` | Maximum unacknowledged messages per consumer |
| `ConcurrencyLimit` | `1` | Maximum number of handlers running in parallel for a consumer |
| `AutoAck` | `false` | Whether the broker auto-acknowledges on delivery |
| `QueueDurable` | `true` | Whether the queue survives broker restarts |
| `QueueExclusive` | `false` | Whether the queue is exclusive to this connection |
| `QueueAutoDelete` | `false` | Whether the queue is deleted when the last consumer disconnects |
| `ExchangeDurable` | `true` | Whether the exchange survives broker restarts |

```csharp
.WithRabbitMq(r => r
    .WithQueueName("orders.handler")
    .WithPrefetch(50)
    .WithConcurrencyLimit(10)
    .WithDurableQueue())
```

`ConcurrencyLimit` must be less than or equal to `PrefetchCount` (unless `PrefetchCount` is `0`, which means unlimited prefetch in RabbitMQ).

## Health Checks

The RabbitMQ transport registers a `RabbitMqConsumerHealthCheck` that reports the consumer's connection state. It's automatically available when using ASP.NET Core health checks:

```csharp
app.MapHealthChecks("/health");
```

## Reconnection

The `RabbitMqConsumer` automatically reconnects with exponential backoff (1 second to 30 seconds with jitter) when the connection to RabbitMQ is lost. No manual intervention is required for transient network issues.

## Graceful shutdown

On application shutdown, `RabbitMqConsumer` stops new deliveries before closing AMQP channels:

1. **Cancel consumers** — `BasicCancel` for each subscription so the broker stops sending new messages.
2. **Drain in-flight handlers** — waits until running `RouteAsync` / handler work completes (up to `RabbitMqOptions.ShutdownDrainTimeout`, default **30 seconds**).
3. **Close channels** — after the execute loop stops and handlers have drained.

Set `ShutdownDrainTimeout` if handlers can run longer than the default. Also set the host’s [`HostOptions.ShutdownTimeout`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.hostoptions.shutdowntimeout) (default 30 seconds) so the process is not torn down while the consumer is still draining.

## What's Next

- [EF Core Transport](efcore-transport.md) — Database-based message delivery without a broker
- [Outbox](outbox.md) — Combine RabbitMQ with the transactional outbox for reliable publishing
- [Operations](operations.md) — Monitoring RabbitMQ consumers and handling disconnections
