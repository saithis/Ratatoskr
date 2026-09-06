# Management & UI Dashboard

Ratatoskr provides an optional, distributed management control plane and web dashboard:
- **`Ratatoskr.Management`**: A lightweight agent embedded in microservices that collects metrics, periodically announces health heartbeats, and processes management commands.
- **`Ratatoskr.UI`**: A self-contained, embedded Single-Page Application (SPA) web dashboard with real-time updates via Server-Sent Events (SSE).

Together, they allow operators and developers to monitor inbox and outbox health across all services, inspect CloudEvents payloads and handler failure details, and execute single or bulk retry/discard actions for failed and poisoned messages.

---

## Architecture & Topologies

```
┌────────────────────────────────────────────────────────────────────────┐
│                          Ratatoskr.UI Dashboard                        │
│  (Embedded SPA Web Host · Server-Sent Events · REST Proxy API)         │
└───────────────▲───────────────────────────────────────┬────────────────┘
                │ Heartbeats & RPC Replies              │ Commands ({serviceId}.{action})
                │ ({uiUser}.inbox)                      │ ({uiUser}.commands)
┌───────────────┴───────────────────────────────────────▼────────────────┐
│                       RabbitMQ Message Broker                          │
│                                                                        │
│   Exchange: {uiUser}.commands (Topic)                                  │
│   Exchange: {uiUser}.inbox    (Topic)                                  │
└───────────────▲───────────────────────────────────────┬────────────────┘
                │                                       │
     ┌──────────┴──────────┐                 ┌──────────▼──────────┐
     │  Orders Service     │                 │  Billing Service    │
     │  (Ratatoskr.Mgmt)   │                 │  (Ratatoskr.Mgmt)   │
     │  Queue: orders.mgmt │                 │  Queue: billing.mgmt│
     └─────────────────────┘                 └─────────────────────┘
```

### Strict ACL Compliant (Broker Mode)

In distributed architectures, inter-service HTTP communication is often blocked by network policies, ingress firewalls, or service mesh restrictions. Ratatoskr Management solves this by communicating **entirely over the message broker**.

It strictly adheres to restrictive RabbitMQ user permission regex patterns:

| Permission | Pattern | Meaning |
|---|---|---|
| **configure** | `{user}\..*` | Declare or delete only own resources. |
| **write** | `{user}\..*\|.*\.inbox$` | Publish to own resources and any `*.inbox`. |
| **read** | `{user}\..*\|.*(?<!internal)$` | Consume own resources and any non-internal resource. |

#### Two-Exchange Topology
1. **`{uiUser}.commands` (Topic Exchange)**:
   - Declared by the UI host.
   - The UI publishes commands with routing key `{serviceId}.{action}` (e.g., `orders.RequeueOutbox`).
   - Each service declares its private queue `{serviceId}.mgmt` (allowed under `{user}\..*`) and binds it to `{uiUser}.commands` for `{serviceId}.#` and `*.broadcast`.
2. **`{uiUser}.inbox` (Topic Exchange)**:
   - Declared by the UI host. The exchange name ends in `.inbox`, granting every microservice write permissions via `.*\.inbox$`.
   - Services publish periodic heartbeats (`routingKey: heartbeat`, type `ratatoskr.management.heartbeat`).
   - Services publish RPC replies (`routingKey: reply.{requestId}`, carrying AMQP `CorrelationId`).
   - The UI binds a private queue to `{uiUser}.inbox` to receive heartbeats and correlate RPC replies.

### In-Process Mode (Modular Monoliths & EF Core Transport)

If your application uses the EF Core transport without a message broker (e.g., in a modular monolith or during local development), `Ratatoskr.UI` and `Ratatoskr.Management` operate in **In-Process Mode**. 

The UI detects the local handler and dispatches queries and commands directly within the process. It automatically discovers and aggregates metrics across multiple `DbContext` instances registered in the service.

---

## Setting Up Microservices (`Ratatoskr.Management`)

Install the agent package in any microservice using Ratatoskr:

```bash
dotnet add package Ratatoskr.Management
```

### Registration

Configure the management agent in `Program.cs`:

```csharp
// 1. Configure Ratatoskr durability and transport
builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c => c.ConnectionString = new Uri("amqp://..."));
    bus.AddEfCoreDurability<OrdersDbContext>(d => d.UseInbox().UseOutbox());
});

// 2. Add Ratatoskr Management agent
builder.Services.AddRatatoskrManagement(options =>
{
    options.ServiceName = "orders-service";
    options.InstanceId = Environment.MachineName; // or Pod/Container ID
    options.UiExchangePrefix = "ratatoskr.ui";    // matches UI host user
    options.HeartbeatInterval = TimeSpan.FromSeconds(15);
    options.EnableHeartbeat = true;
});
```

### Configuration Options

| Option | Type | Default | Description |
|---|---|---|---|
| `ServiceName` | `string` | *(Assembly Name)* | Unique logical name of the service. |
| `InstanceId` | `string` | `Guid.NewGuid()` | Identifier for the specific replica/node. |
| `UiExchangePrefix` | `string` | `"ratatoskr.ui"` | Name prefix for the UI commands/inbox exchanges. |
| `HeartbeatInterval` | `TimeSpan` | `15 seconds` | Frequency of background heartbeat announcements. |
| `EnableHeartbeat` | `bool` | `true` | Set to `false` when running in a pure in-process monolith. |

---

## Setting Up the Dashboard (`Ratatoskr.UI`)

The dashboard can run as a **standalone service** or **embedded** within an existing ASP.NET Core application.

Install the UI package:

```bash
dotnet add package Ratatoskr.UI
```

### Registration and Route Mapping

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Connect to RabbitMQ (if using broker mode)
builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c => c.ConnectionString = new Uri("amqp://..."));
});

// 2. Define an authorization policy (mandatory)
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("RatatoskrAdmin", policy =>
    {
        policy.RequireRole("PlatformAdmin");
    });

// 3. Register UI services
builder.Services.AddRatatoskrUI(options =>
{
    options.UiExchangePrefix = "ratatoskr.ui";
    options.RequestTimeout = TimeSpan.FromSeconds(15);
    options.ServiceOfflineThreshold = TimeSpan.FromSeconds(45);
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// 4. Mount the UI dashboard under a URL path
app.MapRatatoskrUI("RatatoskrAdmin", "/ratatoskr");

app.Run();
```

> [!IMPORTANT]
> **Mandatory Authorization Policy**  
> `MapRatatoskrUI` requires a valid ASP.NET Core authorization policy name at compile/startup time. If the policy is not registered, an `InvalidOperationException` is thrown during application startup.

### Configuration Options

| Option | Type | Default | Description |
|---|---|---|---|
| `UiExchangePrefix` | `string` | `"ratatoskr.ui"` | Prefix used for declaring `.commands` and `.inbox` exchanges. |
| `RequestTimeout` | `TimeSpan` | `15 seconds` | Maximum wait time for RPC responses from microservices. |
| `ServiceOfflineThreshold` | `TimeSpan` | `45 seconds` | Elapsed time without a heartbeat before marking a replica offline. |

---

## Dashboard Features & User Guide

Once mounted, navigate to `http://<host>:<port>/ratatoskr/` in your browser.

### 1. Connected Services Overview
- Displays all registered microservices with real-time status badges (**Online**, **Stale**, or **Offline**).
- Shows active replica count and aggregated pending vs. poisoned message counters across all `DbContext`s.
- Automatic live updates powered by Server-Sent Events (SSE).

### 2. Service Replicas & Multi-DbContext Details
- **Active Replicas**: Lists individual running instances, host/machine name, environment, start time, and last heartbeat timestamp.
- **Database Contexts**: Breaks down pending and poisoned counts for every `IOutboxDbContext` and `IInboxDbContext` in the service.

### 3. Outbox Inspector
- **Filtering**: View messages by status (`Poisoned`, `Pending`, `Processed`, or `All`).
- **Keyset Pagination**: Fast navigation through outbox queues.
- **Message Detail Modal**: Click on any message to inspect its metadata, CloudEvents attributes (`type`, `source`, `id`, `time`, `traceparent`), error message, and decoded JSON payload.
- **Single Requeue / Discard**: Retry a poisoned outbox message (resets error count, unpoisons, and schedules immediate publish) or discard it.
- **Bulk Operations**: Bulk requeue or bulk delete all poisoned outbox messages for a selected DbContext in one click.

### 4. Inbox Inspector
- **Per-Handler Status**: Displays consumer messages and the exact status of each attached handler (`HandlerKey`, attempt count, last error, timestamps).
- **Poison Inspection**: See exactly which handler failed and the full exception stack trace.
- **Retry Options**:
  - **Requeue Specific Handler**: Retries only the failing handler without re-running handlers that already completed successfully.
  - **Requeue Entire Message**: Resets all handler statuses for the message.
- **Bulk Operations**: Bulk requeue or discard all poisoned inbox handlers.

### 5. Channels & Topology
- Visualizes all publish and consume channels registered by the service.
- Displays channel intent (`EventPublish`, `CommandConsume`, etc.), transport details (`rabbitmq` / `efcore`), destination exchange/queue names, and accepted message types.
