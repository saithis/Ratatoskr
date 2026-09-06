# Configuration Reference

This page provides a quick-reference index of all configuration options across Ratatoskr. Each table links to the feature page where the option is documented in detail.

## Core

```csharp
builder.Services.AddRatatoskr(bus =>
{
    // All configuration happens here
});
```

### Channel Methods

| Method | Intent | Details |
|--------|--------|---------|
| `AddEventPublishChannel(name, ...)` | Publish events you own | [Channels & Routing](channels-routing.md) |
| `AddCommandPublishChannel(name, ...)` | Send commands to others | [Channels & Routing](channels-routing.md) |
| `AddEventConsumeChannel(name, ...)` | Subscribe to others' events | [Channels & Routing](channels-routing.md) |
| `AddCommandConsumeChannel(name, ...)` | Process commands you own | [Channels & Routing](channels-routing.md) |

### Message & Handler Registration

| Method | Description | Details |
|--------|-------------|---------|
| `.Produces<T>()` | Register a message type on a publish channel | [Channels & Routing](channels-routing.md) |
| `.Consumes<T>(m => ...)` | Register a message type and handlers on a consume channel | [Messages & Handlers](messages-handlers.md) |
| `.WithSerializer<TSerializer>()` | Use a serializer for a specific message type | [Messages & Handlers](messages-handlers.md) |
| `.WithHandler<T>()` | Fire-and-forget handler (no persistence) | [Messages & Handlers](messages-handlers.md) |
| `.WithHandler<T>("key")` | Inbox-managed handler with stable key | [Inbox](inbox.md) |
| `.AllowConsumeWithoutInbox()` | Explicit opt-out from optional consume-channel inbox requirement policy | [Inbox](inbox.md) |

### Attributes

| Attribute | Target | Description | Details |
|-----------|--------|-------------|---------|
| `[RatatoskrMessage("type")]` | Class/Record | Sets the CloudEvents `type` and default routing key | [Messages & Handlers](messages-handlers.md) |
| `[AsyncApiMessage(...)]` | Class/Record | Adds AsyncAPI documentation metadata | [AsyncAPI](asyncapi.md) |

## CloudEvents

```csharp
bus.ConfigureCloudEvents(ce =>
{
    ce.DefaultSource = "/order-service";
    ce.ContentMode = CloudEventsContentMode.Binary;
});
```

| Property | Default | Description | Details |
|----------|---------|-------------|---------|
| `DefaultSource` | `"/"` | CloudEvents `source` URI | [CloudEvents](cloudevents.md) |
| `ContentMode` | `Binary` | Wire encoding mode (`Binary` or `Structured`) | [CloudEvents](cloudevents.md) |

## RabbitMQ Transport

```csharp
bus.UseRabbitMq(c =>
{
    c.ConnectionString = new Uri("amqp://guest:guest@localhost:5672/");
    c.MaxInboundMessageSize = 1048576; // 1 MB limit
});
```

### Global Options

| Option | Default | Description | Details |
|--------|---------|-------------|---------|
| `ConnectionString` | — | RabbitMQ connection URI | [RabbitMQ](rabbitmq.md) |
| `UsePublisherConfirms` | `true` | Wait for publisher acks | [RabbitMQ](rabbitmq.md) |
| `MaxInboundMessageSize` | *none* | Max body size in bytes before rejection | [RabbitMQ](rabbitmq.md) |

### Channel Options

| Option | Default | Description | Details |
|--------|---------|-------------|---------|
| `WithTopicExchange()` | Topic | Pattern-based routing | [RabbitMQ](rabbitmq.md) |
| `WithDirectExchange()` | — | Exact routing key match | [RabbitMQ](rabbitmq.md) |
| `WithFanoutExchange()` | — | Broadcast to all queues | [RabbitMQ](rabbitmq.md) |
| `WithQueueName(string)` | (required) | Queue to consume from | [RabbitMQ](rabbitmq.md) |
| `WithPrefetch(ushort)` | `10` | Max unacknowledged messages | [RabbitMQ](rabbitmq.md) |
| `WithQueueType(QueueType)` | `Quorum` | Queue implementation (Quorum/Classic) | [RabbitMQ](rabbitmq.md) |
| `WithAutoAck(bool)` | `false` | Auto-acknowledge on delivery | [RabbitMQ](rabbitmq.md) |
| `WithDurableQueue()` | (default) | Survives broker restarts | [RabbitMQ](rabbitmq.md) |
| `WithTransientQueue()` | — | Deleted when the last consumer disconnects | [RabbitMQ](rabbitmq.md) |

### Retry Options

| Option | Default | Description | Details |
|--------|---------|-------------|---------|
| `WithMaxRetries(int)` | `3` | Attempts before DLQ | [RabbitMQ](rabbitmq.md) |
| `WithDelay(TimeSpan)` | `30s` | TTL on retry queue | [RabbitMQ](rabbitmq.md) |
| `WithManaged(bool)` | `true` | Auto-provision retry/DLQ topology | [RabbitMQ](rabbitmq.md) |
| `WithDeadLetterSuffix(string)` | `".dlq"` | DLQ name suffix | [RabbitMQ](rabbitmq.md) |
| `WithRetrySuffix(string)` | `".retry"` | Retry queue name suffix | [RabbitMQ](rabbitmq.md) |

## EF Core Durability

```csharp
bus.AddEfCoreDurability<OrderDbContext>(d =>
{
    d.UseOutbox(outbox => { /* options */ });
    d.UseInbox(inbox => { /* options */ });
});
```

### Durability builder (shared)

| Option | Default | Description | Details |
|--------|---------|-------------|---------|
| `WithMetricsPollingInterval(TimeSpan)` | `30s` | How often backlog gauge counts are refreshed from the database | [Observability](observability.md) |
| `WithMetricsQueryTimeout(TimeSpan)` | `5s` | Per-`COUNT` query cancellation timeout for those refreshes | [Observability](observability.md) |

### Outbox Options

| Option | Default | Description | Details |
|--------|---------|-------------|---------|
| `WithMaxRetries(int)` | `5` | Send attempts before poisoned | [Outbox](outbox.md) |
| `WithMaxRetryDelay(TimeSpan)` | `5 min` | Max backoff delay | [Outbox](outbox.md) |
| `WithPollingInterval(TimeSpan)` | `60s` | Idle polling interval | [Outbox](outbox.md) |
| `WithBatchSize(int)` | `100` | Messages per batch | [Outbox](outbox.md) |
| `WithStuckMessageThreshold(TimeSpan)` | `5 min` | Stuck detection timeout | [Outbox](outbox.md) |
| `WithSendTimeout(TimeSpan)` | *none* | Send operation timeout | [Outbox](outbox.md) |
| `WithMaxMessageSize(int)` | *none* | Max body size in bytes | [Outbox](outbox.md) |
| `WithRestartDelay(TimeSpan)` | `5s` | Processor restart delay | [Outbox](outbox.md) |
| `WithLockAcquireTimeout(TimeSpan)` | `60s` | Lock acquire timeout | [Outbox](outbox.md) |
| `WithLockName(string)` | `"OutboxProcessor_{DbContext}"` | Distributed lock name | [Outbox](outbox.md) |
| `WithRetention(TimeSpan)` | *none* | Auto-cleanup retention | [Outbox](outbox.md) |
| `WithCleanupInterval(TimeSpan)` | `1h` | Cleanup run interval | [Outbox](outbox.md) |
| `WithCleanupBatchSize(int)` | `10,000` | Cleanup batch size | [Outbox](outbox.md) |
| `WithCleanupLockName(string)` | `"OutboxCleanup_{DbContext}"` | Cleanup distributed lock name | [Operations](operations.md) |
| `WithoutBackgroundProcessing()` | — | Disable background service (testing) | [Testing](testing.md) |

### Inbox Options

| Option | Default | Description | Details |
|--------|---------|-------------|---------|
| `WithMaxRetries(int)` | `5` | Delivery attempts before poisoned | [Inbox](inbox.md) |
| `WithMaxRetryDelay(TimeSpan)` | `5 min` | Max backoff delay (with jitter) | [Inbox](inbox.md) |
| `WithPollingInterval(TimeSpan)` | `30s` | Idle polling interval | [Inbox](inbox.md) |
| `WithBatchSize(int)` | `100` | Handler statuses per batch | [Inbox](inbox.md) |
| `WithStuckMessageThreshold(TimeSpan)` | `5 min` | Stuck detection timeout | [Inbox](inbox.md) |
| `WithHandlerTimeout(TimeSpan)` | *none* | Handler execution timeout | [Inbox](inbox.md) |
| `WithRestartDelay(TimeSpan)` | `5s` | Processor restart delay | [Inbox](inbox.md) |
| `WithLockAcquireTimeout(TimeSpan)` | `60s` | Lock acquire timeout | [Inbox](inbox.md) |
| `WithLockName(string)` | `"InboxProcessor_{DbContext}"` | Distributed lock name | [Inbox](inbox.md) |
| `WithRetention(TimeSpan)` | *none* | Auto-cleanup retention | [Inbox](inbox.md) |
| `WithCleanupInterval(TimeSpan)` | `1h` | Cleanup run interval | [Inbox](inbox.md) |
| `WithCleanupBatchSize(int)` | `10,000` | Cleanup batch size | [Inbox](inbox.md) |
| `WithCleanupLockName(string)` | `"InboxCleanup_{DbContext}"` | Cleanup distributed lock name | [Operations](operations.md) |
| `WithConsumeChannelInboxRequirement(ConsumeChannelInboxRequirement)` | `None` | Optional safeguard: warn/fail when consume channels omit `UseInbox()` | [Inbox](inbox.md) |
| `WithoutBackgroundProcessing()` | — | Disable background service (testing) | [Testing](testing.md) |

## AsyncAPI

```csharp
bus.ConfigureAsyncApi(api =>
{
    api.WithTitle("Order Processing API");
    api.WithVersion("1.0.0");
    api.WithDescription("Order processing messaging API");
});
```

| Method | Description | Details |
|--------|-------------|---------|
| `WithTitle(string)` | API document title | [AsyncAPI](asyncapi.md) |
| `WithVersion(string)` | API version | [AsyncAPI](asyncapi.md) |
| `WithDescription(string)` | API description | [AsyncAPI](asyncapi.md) |
| `app.MapAsyncApi()` | Map the AsyncAPI JSON endpoint | [AsyncAPI](asyncapi.md) |

## OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(RatatoskrDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(RatatoskrDiagnostics.MeterName));
```

| Constant | Value | Description | Details |
|----------|-------|-------------|---------|
| `RatatoskrDiagnostics.ActivitySourceName` | `"Ratatoskr"` | Tracing activity source | [Observability](observability.md) |
| `RatatoskrDiagnostics.MeterName` | `"Ratatoskr"` | Metrics meter | [Observability](observability.md) |

## Testing

```csharp
services.AddRatatoskrTesting();
```

| Method | Description | Details |
|--------|-------------|---------|
| `AddRatatoskrTesting()` | Register the message tracker | [Testing](testing.md) |
| `CreateTrackingSession()` | Create a trace-isolated test session | [Testing](testing.md) |
| `TrackActivity()` | Start the action-based tracking API | [Testing](testing.md) |

## Management Agent (`Ratatoskr.Management`)

```csharp
services.AddRatatoskrManagement(options =>
{
    options.ServiceName = "orders-service";
    options.InstanceId = Environment.MachineName;
    options.UiExchangePrefix = "ratatoskr.ui";
    options.HeartbeatInterval = TimeSpan.FromSeconds(15);
    options.EnableHeartbeat = true;
});
```

| Property | Default | Description | Details |
|---|---|---|---|
| `ServiceName` | Assembly Name | Logical name of the service | [Management & UI](management-ui.md) |
| `InstanceId` | `Guid.NewGuid()` | Identifier for this specific replica | [Management & UI](management-ui.md) |
| `UiExchangePrefix` | `"ratatoskr.ui"` | Name prefix for UI command & inbox exchanges | [Management & UI](management-ui.md) |
| `HeartbeatInterval` | `15 seconds` | Heartbeat announcement broadcast interval | [Management & UI](management-ui.md) |
| `EnableHeartbeat` | `true` | Set to `false` in in-process monoliths without broker | [Management & UI](management-ui.md) |

## Management UI Dashboard (`Ratatoskr.UI`)

```csharp
services.AddRatatoskrUI(options =>
{
    options.UiExchangePrefix = "ratatoskr.ui";
    options.RequestTimeout = TimeSpan.FromSeconds(15);
    options.ServiceOfflineThreshold = TimeSpan.FromSeconds(45);
});

// In pipeline:
app.MapRatatoskrUI("RatatoskrAdmin", "/ratatoskr");
```

| Property | Default | Description | Details |
|---|---|---|---|
| `UiExchangePrefix` | `"ratatoskr.ui"` | Name prefix for UI command & inbox exchanges | [Management & UI](management-ui.md) |
| `RequestTimeout` | `15 seconds` | Maximum wait timeout for RPC command replies | [Management & UI](management-ui.md) |
| `ServiceOfflineThreshold` | `45 seconds` | Time without heartbeats before marking offline | [Management & UI](management-ui.md) |

## Distributed Lock Provider

The lock provider is registered as `IDistributedLockProvider` in DI. See [Operations](operations.md) for provider options (File, PostgreSQL, SQL Server, Redis).

