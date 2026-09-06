# Ratatoskr.Management

Lightweight management agent and RPC control plane for Ratatoskr CloudEventBus. Allows microservices to report heartbeats and statistics, and execute outbox and inbox operations requested by `Ratatoskr.UI`.

## Features

- **Decoupled Over-the-Broker Communication**: Communicates with `Ratatoskr.UI` via RabbitMQ (or in-process direct dispatch for EF Core modular monoliths).
- **Strict ACL Compliant**: Adheres to restrictive RabbitMQ user permission regexes (`configure: {user}\..*`, `write: {user}\..*|.*\.inbox$`, `read: {user}\..*|.*(?<!internal)$`).
- **Two-Exchange Architecture**:
  - Consumes commands from `{uiUser}.commands` on service queue `{user}.mgmt` (`routingKey: {user}.#` and `*.broadcast`).
  - Emits periodic heartbeat and RPC replies to `{uiUser}.inbox`.
- **Multi-DbContext Support**: Queries, inspects, requeues, and discards failed/poisoned messages across any number of `IOutboxDbContext` and `IInboxDbContext` instances.

## Getting Started

Install the package via NuGet:

```bash
dotnet add package Ratatoskr.Management
```

Register the management agent in your microservice:

```csharp
builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c => c.ConnectionString = new Uri("amqp://..."));
    bus.AddEfCoreDurability<OrdersDbContext>(d => d.UseInbox().UseOutbox());
});

builder.Services.AddRatatoskrManagement(options =>
{
    options.ServiceName = "orders-service";
    options.InstanceId = Environment.MachineName; // or pod ID
    options.UiExchangePrefix = "ratatoskr.ui"; // matches UI user
    options.HeartbeatInterval = TimeSpan.FromSeconds(15);
    options.EnableHeartbeat = true;
});
```
