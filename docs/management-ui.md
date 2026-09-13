# Management UI and Control Plane

Ratatoskr includes an optional, transport-neutral management control plane and embedded web dashboard. It allows operators to inspect service instances, monitor inbox and outbox message processing, triage and retry poisoned messages, and review audit trails across multiple services and transports.

The architecture strictly decouples the management control plane from application messaging:
- `Ratatoskr.UI` contains the embedded dashboard, REST facade, Server-Sent Events (SSE), and authorization policies.
- It does **not** reference RabbitMQ or application message transports.
- Transports (in-process or RabbitMQ) implement provider interfaces (`IManagementTransport`, `IManagementDiscoverySource`), enabling hybrid topologies, zero-broker local development, and distributed production control planes without modifying dashboard code or shared contracts.

---

## Package Graph

Management capabilities are structured into 3 focused packages alongside core library integrations:

| Package | Purpose | Dependencies |
|---|---|---|
| `Ratatoskr.Management.Abstractions` | Versioned protocol envelopes, stable error codes, cursor contracts, capability/topology models, and transport interfaces (`IManagementTransport`, `IManagementDiscoverySource`, `IManagementOperation`, `IChannelQueueResolver`). | None (pure contracts) |
| `Ratatoskr.Management.RabbitMq` | RabbitMQ control-plane transport provider: dedicated connection, least-privilege AMQP topology (`*.inbox` exchanges, durable service queues, exclusive instance and reply queues), caller authentication (`user_id` and HMAC). | `Ratatoskr.Management.Abstractions`, `RabbitMQ.Client` |
| `Ratatoskr.UI` | Web dashboard: vanilla modular ES frontend, dashboard database store (`RatatoskrDashboardDbContext`), audit logging and retention, SSE service events, and antiforgery. | `Ratatoskr`, `Ratatoskr.Management.Abstractions` |

### Consolidated Runtime Capabilities

Rather than requiring separate management packages per feature:
- **Core Agent & In-Process Control Plane**: Built into `Ratatoskr` core. Register via `bus.UseManagement(agent => ...)` inside `AddRatatoskr`.
- **EF Core Operations**: Built into `Ratatoskr.EfCore`. Inbox/outbox queries, mutations, idempotency log, and retention workers are automatically registered by `bus.AddEfCoreDurability<TContext>()`.
- **RabbitMQ Queue Metrics & DLQ Operations**: Built into `Ratatoskr.RabbitMq`. Main queue depth, dead letter queue inspection, batch requeueing, and queue purging are automatically registered by `bus.UseRabbitMq()`.

---

## Architecture Overview

```
                        +---------------------------------------------+
                        |           Ratatoskr UI Dashboard            |
                        |  (Modular ES Frontend + SSE + Audit Store)  |
                        +----------------------+----------------------+
                                               |
                          +--------------------+--------------------+
                          | IManagementTransportRegistry            |
                          +--------------------+--------------------+
                                               |
                  +----------------------------+----------------------------+
                  |                                                         |
         [ In-Process Transport ]                                 [ RabbitMQ Transport ]
                  |                                                         |
                  v                                                         v
       +--------------------+                                    +--------------------+
       | Local Agent Host   |                                    | Remote Agent Host  |
       |  (In-Memory Dispatch)                                   | (AMQP Inbox Queues)|
       +---------+----------+                                    +---------+----------+
                 |                                                         |
                 v                                                         v
     +-----------------------+                                 +-----------------------+
     | IManagementDispatcher |                                 | IManagementDispatcher |
     +-----------+-----------+                                 +-----------+-----------+
                 |                                                         |
        [ Operations Layer ]                                      [ Operations Layer ]
   (Outbox / Inbox / DbContext)                               (Outbox / Inbox / DbContext)
```

---

## Quick Start: Co-hosted (In-Process) Dashboard

For single-service applications, modular monoliths, or local development, you can host the dashboard and management agent in the same process without external broker dependencies.

### 1. Register Services

```csharp
// 1. Add the Management Agent
builder.Services.AddRatatoskrManagementAgent(agent =>
{
    agent.ServiceName = "orders-service";
    agent.InstanceId = Environment.MachineName;
    agent.Configure(options =>
    {
        options.HeartbeatInterval = TimeSpan.FromSeconds(15);
    });

    // Register in-process transport
    agent.AddInProcess("local");
});

// 2. Add the Management UI Dashboard
builder.Services.AddRatatoskrUI(dashboard =>
{
    dashboard.Configure(options =>
    {
        options.StaleAfter = TimeSpan.FromSeconds(45);
        options.AutoMigrate = true;
    });

    // Dashboard storage (SQLite for local/testing, or PostgreSQL for production)
    dashboard.UseSqlite("Data Source=ratatoskr-dashboard.db");

    // Connect dashboard to the in-process transport
    dashboard.AddInProcess("local");
});

// 3. Configure Authorization Policies
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("RatatoskrMetadata", policy => policy.RequireRole("Operator", "Administrator"))
    .AddPolicy("RatatoskrPayloads", policy => policy.RequireRole("Operator", "Administrator"))
    .AddPolicy("RatatoskrRequeue", policy => policy.RequireRole("Operator", "Administrator"))
    .AddPolicy("RatatoskrDelete", policy => policy.RequireRole("Administrator"))
    .AddPolicy("RatatoskrBulk", policy => policy.RequireRole("Administrator"));
```

### 2. Map Endpoints in HTTP Pipeline

```csharp
var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Map UI dashboard under /ratatoskr
app.MapRatatoskrUI(
    new RatatoskrUiAuthorizationPolicies(
        Metadata: "RatatoskrMetadata",
        Payloads: "RatatoskrPayloads",
        Requeue: "RatatoskrRequeue",
        Delete: "RatatoskrDelete",
        Bulk: "RatatoskrBulk"
    ),
    prefix: "/ratatoskr"
);

app.Run();
```

---

## Distributed Setup (RabbitMQ Control Plane)

In a microservices architecture, individual services run `Ratatoskr.Management.RabbitMq` agents, while one or more dedicated dashboard instances connect over RabbitMQ to discover and manage them.

> [!IMPORTANT]
> The management control plane uses its own dedicated AMQP connection and connection string independent of `bus.UseRabbitMq(...)`. This ensures control-plane traffic is isolated from application message backlogs.

### Managed Service Configuration

Install `Ratatoskr.Management.RabbitMq` and `Ratatoskr.Management.EfCore`:

```csharp
builder.Services.AddRatatoskrManagementAgent(agent =>
{
    agent.ServiceName = "inventory-service";
    agent.InstanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}"[..16];

    agent.AddRabbitMq("primary-broker", rmq =>
    {
        rmq.ConnectionString = new Uri("amqp://inventory:secret@rabbitmq:5672/");
        rmq.ResourcePrefix = "inventory";
        rmq.DiscoveryExchange = "dashboard.mgmt.discovery.inbox";

        // Security: only accept commands from authorized dashboard identities
        rmq.AllowedCallers.Add("dashboard");
    });
});
```

### Central Dashboard Configuration

Install `Ratatoskr.UI` and `Ratatoskr.Management.RabbitMq`:

```csharp
builder.Services.AddRatatoskrUI(dashboard =>
{
    dashboard.Configure(options =>
    {
        options.StaleAfter = TimeSpan.FromSeconds(30);
        options.PruneAfter = TimeSpan.FromHours(2);
        options.AutoMigrate = true;
    });

    // Central dashboard database
    dashboard.UseNpgsql(builder.Configuration.GetConnectionString("DashboardDb"));

    // Connect to the RabbitMQ control plane
    dashboard.AddRabbitMq("primary-broker", rmq =>
    {
        rmq.ConnectionString = new Uri("amqp://dashboard:secret@rabbitmq:5672/");
        rmq.ResourcePrefix = "dashboard";
        rmq.ReplicaId = Environment.MachineName;
    });
});
```

---

## Dashboard Persistence & Migrations

The dashboard stores service discovery state, instance topology, capability manifests, and audit logs in `RatatoskrDashboardDbContext`.

### Storage Providers

- **SQLite**: `dashboard.UseSqlite(connectionString)` — ideal for single-replica dashboards, testing, and local development.
- **PostgreSQL**: `dashboard.UseNpgsql(connectionString)` — recommended for high-availability multi-replica dashboard clusters.

### Migrations and AutoMigrate

- Set `options.AutoMigrate = true` to automatically apply migrations at dashboard startup.
- `RatatoskrDashboardDbContext` ships with embedded EF Core migrations.
- When sharing a database with application contexts, configure `options.Schema = "ratatoskr_mgmt"` to isolate dashboard tables into a separate database schema.

---

## Multi-Transport Identity & Navigation

The control plane models resources across a clean four-tier hierarchy:

$$\text{Transport} \longrightarrow \text{Service} \longrightarrow \text{Instance} \longrightarrow \text{Context}$$

- **Transport**: The communication channel (`"local"`, `"primary-broker"`, `"eu-broker"`). A dashboard can register multiple transports simultaneously.
- **Service**: The logical application name (e.g. `"orders-service"`).
- **Instance**: A specific replica of the service (e.g. `"orders-pod-4a2f"`).
- **Context**: An EF Core `DbContext` registered in that service managing Outbox/Inbox tables.

### Targeting Modes

1. **Logical Service Targeting**: Commands without an `InstanceId` are routed to the shared durable service queue (`{prefix}.mgmt.cmd.{service}.q`). Any healthy replica competing on that queue can handle the command.
2. **Instance Targeting**: Commands specifying an `InstanceId` route directly to that replica's exclusive instance queue (`{prefix}.mgmt.cmd.{service}.{instance}.q`). If the replica is stopped or disconnected, the broker immediately returns the command as unroutable (`target_unreachable`), failing fast without waiting out the deadline.

---

## Security & Authentication

### Least-Privilege AMQP Permissions

The RabbitMQ control plane operates under strict, locked-down permissions:

| Operation | AMQP Resource | Permission Rule |
|---|---|---|
| Declare queues/exchanges | `{prefix}.*` | Configure: `{user}\..*` |
| Publish heartbeats / replies | `*.inbox` | Write: `{user}\..*\|.*\.inbox$` |
| Consume commands / replies | `{prefix}.*` | Read: `{user}\..*\|.*(?<!internal)$` |

See [RabbitMQ Least-Privilege Guide](rabbitmq.md#least-privilege-permissions) for exact `rabbitmqctl` commands.

### Caller Authentication

Because AMQP write access to `*.inbox` exchanges is broad, agents strictly verify callers:
1. **Broker-Validated `user_id` (Default)**: The broker enforces that the `user_id` message property matches the authenticated AMQP username. Agents verify that `user_id` is present in `AllowedCallers`.
2. **HMAC Signature**: For environments where services share a broker identity or cross untrusted boundaries, configure `options.SharedSecret`. The transport attaches an SHA-256 HMAC signature calculated over request headers and payload.

### Antiforgery

`Ratatoskr.UI` enforces ASP.NET Core Antiforgery protection scoped specifically to cookie-authenticated browser sessions. API requests bearing `Authorization: Bearer <token>` bypass antiforgery validation, allowing external automation and monitoring tools to interact with the management endpoints cleanly.

### Granular Authorization Policies

`RatatoskrUiAuthorizationPolicies` enforces role-based separation across sensitive operations:
- **Metadata**: Viewing service cards, instance lists, channel topology, and message counts.
- **Payloads**: Inspecting serialized message bodies and exception stack traces.
- **Requeue**: Triggering single or matching retries for failed messages.
- **Delete**: Deleting messages from outbox or inbox stores.
- **Bulk**: Performing batch operations and pattern-matched mutations.

---

## Bounded Bulk Mutations & Previews

Bulk operations are preview-first and bounded to prevent accidental database locks or runaway transactions:

1. **Mandatory Filters**: Bulk requests (`*Matching`) require explicit criteria (`Status`, `From`, `To`, `Search`, or specific IDs). Unbounded operations are rejected with HTTP 400.
2. **Two-Phase Preview**:
   - The UI or caller calls `*.count` to query the matching count without mutating.
   - The caller reviews the count and preview sample before confirming execution.
3. **Execution Caps**: Operations are processed in batches (default `100`, max `MaxBatchSize`) up to a hard ceiling (`MaxTotalOperations`, default `10,000`). If more items match, the operation returns `{ processed: 10000, remaining: 2450, capped: true }`.
4. **Idempotency**: All mutations accept an `X-Ratatoskr-Operation-Id` header (or envelope `OperationId`). Retrying a request with the same ID returns the original result without re-executing.

---

## Channels & Dead Letter Queue (DLQ) Management

The **Channels** tab in the dashboard surfaces physical queues, message depths, and Dead Letter Queue (DLQ) metrics for all registered publish and consume channels:

- **Queue Depth**: Real-time message count in the main consumer queue.
- **Dead Letter Queue (DLQ)**: Name and message depth of the associated dead-letter queue (e.g. `{queue}.dlq`).
- **Batch Requeueing**:
  - Operators can choose to requeue a specific batch of messages (e.g., first 100) or all messages currently in the DLQ.
  - Requeued messages are published back to the channel's exchange with their original routing key (CloudEvent `type`).
  - The AMQP `x-death` header is stripped to clear poison tracking, and an `x-requeued-from-dlq-at` timestamp header is attached for observability.
- **Purge DLQ**:
  - Operators can purge all messages from a dead-letter queue after a confirmation prompt.
  - Useful during incident response when poisoned messages have been diagnosed, recorded, or abandoned.

---

## Frontend Architecture

The embedded web dashboard (`src/Ratatoskr.UI/wwwroot/`) is built using modern vanilla ES modules without npm build steps:
- **Zero NPM**: Directly served from embedded assembly resources.
- **Strict CSP**: No `unsafe-inline` or `unsafe-eval`. All dynamic rendering uses `textContent` and safe DOM manipulation (stored-XSS immune).
- **Server-Sent Events (SSE)**: Real-time service discovery updates, instance status transitions, and liveness heartbeats stream over `/api/events`.
