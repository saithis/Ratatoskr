# Ratatoskr.Management

Lightweight management agent and RPC control plane for Ratatoskr CloudEventBus. Allows microservices to report heartbeats and statistics, and execute outbox and inbox operations requested by `Ratatoskr.UI`.

## Features

- **Transport-neutral runtime**: Hosts commands and discovery through provider-neutral contracts, with an in-process provider included.
- **Extensible providers**: Broker and HTTP providers can implement the same command, client, and event contracts without changing operation handlers.
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
    bus.AddEfCoreDurability<OrdersDbContext>(d => d.UseInbox().UseOutbox());
});

builder.Services.AddRatatoskrManagement(options =>
{
    options.ServiceName = "orders-service";
    options.InstanceId = Environment.MachineName; // or pod ID
});
```
