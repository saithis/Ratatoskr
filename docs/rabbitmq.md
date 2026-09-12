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

## Least-privilege permissions

Ratatoskr is designed to run under a narrow, per-identity permission set. For a RabbitMQ user
`u`, these are the only permissions it needs:

| Permission | Pattern |
|---|---|
| configure | `{u}\..*` |
| write | `{u}\..*\|.*\.inbox$` |
| read | `{u}\..*\|.*(?<!internal)$` |

```bash
rabbitmqctl set_permissions -p / orders \
  'orders\..*' \
  'orders\..*|.*\.inbox$' \
  'orders\..*|.*(?<!internal)$'
```

In words: an identity may declare and bind anything under its own `{u}.` prefix, may publish to
its own resources **and to any exchange whose name ends in `.inbox`**, and may read almost
everything. The `.inbox` suffix is the only cross-identity publish channel, which is why every
resource another party has to reach is an exchange named `{receiverPrefix}....inbox`, declared by
the receiver.

### What the broker allows and refuses

The following matrix is verified by `LeastPrivilegeBrokerProbeTests` against `rabbitmq:4.3-alpine`
with exactly the permissions above. Each row is a test, so a broker upgrade that changes one of
these answers fails the build rather than silently breaking the control plane.

| Attempt | Result |
|---|---|
| Publish to the default exchange (`amq.default`) | **Refused**, `403 ACCESS_REFUSED` |
| Declare a server-named (`amq.gen-*`) queue | **Refused**, `403 ACCESS_REFUSED` |
| Declare a transient non-exclusive queue | **Refused** (removed in RabbitMQ 4.1+) |
| Publish to an exchange that has not been declared yet | **Refused**, `404 NOT_FOUND`, **and the channel is closed** |
| Publish with a `user_id` that is not the authenticated user | **Refused**, `406 PRECONDITION_FAILED` |
| Declare a durable queue `{u}.mgmt.cmd.{service}.q` | Allowed |
| Declare a direct exchange `{u}.mgmt.cmd.inbox` and bind a queue to it | Allowed |
| Publish from another identity into `{u}.mgmt.cmd.inbox` | **Allowed and delivered**, with or without `user_id` |
| Declare a **named** exclusive queue under the own prefix | Allowed |
| Two replicas bind named exclusive queues to a fanout `.inbox` and both receive each message | Allowed |
| `mandatory: true` publish to a routing key with no binding | Returned as `312 NO_ROUTE` |
| `mandatory: true` publish to a bound queue with no consumer | Not returned |

### Consequences you have to design for

- **Never publish to the default exchange.** Address a receiver through its own `*.inbox` exchange.
- **Never use server-named or transient non-exclusive queues.** Name every queue `{u}....`, and
  make it either durable or exclusive.
- **The broker is not a confidentiality boundary.** Read access is broad, so any identity in the
  vhost can consume most queues. Management payloads can contain message bodies and exception
  detail; treat everything on the vhost as visible to every identity on it.
- **The broker is not an authentication boundary either.** Write access to `*.inbox` is equally
  broad, so any identity can inject a command into any service's command exchange. Agents
  therefore authenticate the caller themselves — see
  [Management UI](management-ui.md) for `AllowedCallers` and the signed-envelope alternative.
- **`user_id` is trustworthy when present.** The broker validates it against the authenticated
  connection, so it is a free sender identity that needs no shared secret. It is absent unless the
  publisher sets it, so an absent `user_id` must be treated as untrusted, never as trusted.
- **Boot order cannot be controlled.** A publisher cannot declare a receiver-owned exchange, so it
  will publish into a missing exchange whenever the receiver has not started yet, and that closes
  the channel. Any publisher of this kind owns a channel it can afford to lose and retries with
  backoff.

## What's Next

- [EF Core Transport](efcore-transport.md) — Database-based message delivery without a broker
- [Outbox](outbox.md) — Combine RabbitMQ with the transactional outbox for reliable publishing
- [Operations](operations.md) — Monitoring RabbitMQ consumers and handling disconnections
