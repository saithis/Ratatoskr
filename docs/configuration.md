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
services.AddRatatoskrManagementAgent(agent =>
{
    agent.ServiceName = "orders-service";
    agent.InstanceId = Environment.MachineName;
    agent.Configure(options =>
    {
        options.HeartbeatInterval = TimeSpan.FromSeconds(15);
        options.CommandTimeout = TimeSpan.FromSeconds(30);
        options.MaxBatchSize = 100;
        options.MaxTotalOperations = 10_000;
    });

    // In-process transport
    agent.AddInProcess("local");

    // Or RabbitMQ control plane transport (Ratatoskr.Management.RabbitMq)
    agent.AddRabbitMq("primary-broker", options =>
    {
        options.ConnectionString = new Uri("amqp://orders:secret@rabbitmq:5672/");
        options.ResourcePrefix = "orders";
        options.AllowedCallers.Add("dashboard");
    });
});
```

### Agent Options (`ManagementAgentOptions`)

| Property | Default | Description | Details |
|---|---|---|---|
| `ServiceName` | Assembly Name | Logical name of the service | [Management & UI](management-ui.md) |
| `InstanceId` | `Guid.NewGuid()` | Identifier for this specific replica | [Management & UI](management-ui.md) |
| `HeartbeatInterval` | `15 seconds` | Frequency of discovery announcements | [Management & UI](management-ui.md) |
| `CommandTimeout` | `30 seconds` | Maximum execution time per command before cancellation | [Management & UI](management-ui.md) |
| `MaxBatchSize` | `100` | Maximum items processed per transaction batch | [Management & UI](management-ui.md) |
| `MaxTotalOperations` | `10,000` | Hard cap on total operations mutated per matching request | [Management & UI](management-ui.md) |

### Agent RabbitMQ Transport Options (`RabbitMqManagementOptions`)

| Property | Default | Description | Details |
|---|---|---|---|
| `ConnectionString` | — | AMQP URI for the management control plane | [Management & UI](management-ui.md) |
| `ResourcePrefix` | Connection user name | Resource prefix for declared command queues | [Management & UI](management-ui.md) |
| `DiscoveryExchange` | `"dashboard.mgmt.discovery.inbox"` | Fanout exchange to send discovery heartbeats to | [Management & UI](management-ui.md) |
| `AllowedCallers` | *empty* | List of AMQP `user_id` identities authorized to send commands | [Management & UI](management-ui.md) |
| `SharedSecret` | *none* | Optional HMAC-SHA256 secret for cryptographic caller verification | [Management & UI](management-ui.md) |
| `HeartbeatInterval` | `15 seconds` | Periodic heartbeat transmission interval | [Management & UI](management-ui.md) |
| `CommandTimeout` | `30 seconds` | Time allowed for incoming command handling | [Management & UI](management-ui.md) |
| `PrefetchCount` | `10` | Bounded prefetch for command consumption | [Management & UI](management-ui.md) |

## Management UI Dashboard (`Ratatoskr.UI`)

```csharp
services.AddRatatoskrUI(dashboard =>
{
    dashboard.Configure(options =>
    {
        options.StaleAfter = TimeSpan.FromSeconds(45);
        options.PruneAfter = TimeSpan.FromHours(2);
        options.AutoMigrate = true;
        options.AuditRetention = TimeSpan.FromDays(90);
    });

    // Store (SQLite or PostgreSQL)
    dashboard.UseNpgsql(connectionString);

    // RabbitMQ transport (Ratatoskr.Management.RabbitMq)
    dashboard.AddRabbitMq("primary-broker", options =>
    {
        options.ConnectionString = new Uri("amqp://dashboard:secret@rabbitmq:5672/");
        options.ResourcePrefix = "dashboard";
        options.ReplicaId = Environment.MachineName;
    });
});

// In pipeline:
app.MapRatatoskrUI(policies, "/ratatoskr");
```

### Dashboard Options (`ManagementDashboardOptions`)

| Property | Default | Description | Details |
|---|---|---|---|
| `StaleAfter` | `45 seconds` | Time without heartbeats before an instance is marked Stale | [Management & UI](management-ui.md) |
| `PruneAfter` | `2 hours` | Time without heartbeats before a stale instance is pruned | [Management & UI](management-ui.md) |
| `AutoMigrate` | `false` | Automatically apply `RatatoskrDashboardDbContext` migrations on startup | [Management & UI](management-ui.md) |
| `Schema` | *none* | Database schema for dashboard tables (e.g. `"ratatoskr_mgmt"`) | [Management & UI](management-ui.md) |
| `AuditRetention` | `90 days` | Retention window for dashboard audit log entries | [Management & UI](management-ui.md) |
| `AuditPruneInterval` | `1 hour` | Background worker execution interval for audit cleanup | [Management & UI](management-ui.md) |
| `AuditPruneBatchSize` | `1,000` | Maximum audit rows pruned per deletion transaction | [Management & UI](management-ui.md) |

### Dashboard RabbitMQ Transport Options (`RabbitMqManagementOptions`)

| Property | Default | Description | Details |
|---|---|---|---|
| `ConnectionString` | — | AMQP URI for the management control plane | [Management & UI](management-ui.md) |
| `ResourcePrefix` | Connection user name | Prefix used for declaring discovery & reply exchanges | [Management & UI](management-ui.md) |
| `ReplicaId` | `Guid.NewGuid()` | Unique replica ID for binding dedicated reply queues | [Management & UI](management-ui.md) |
| `RequestTimeout` | `30 seconds` | Maximum wait time for command execution replies | [Management & UI](management-ui.md) |
| `SharedSecret` | *none* | Optional HMAC-SHA256 secret for signing outbound commands | [Management & UI](management-ui.md) |
| `PrefetchCount` | `16` | Bounded prefetch for incoming reply consumption | [Management & UI](management-ui.md) |

## Distributed Lock Provider

The lock provider is registered as `IDistributedLockProvider` in DI. See [Operations](operations.md) for provider options (File, PostgreSQL, SQL Server, Redis).
