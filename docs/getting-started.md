# Getting Started

This guide walks you through building a working Ratatoskr application from scratch. By the end, you will have a message producer and consumer running with RabbitMQ and the transactional outbox.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- [Docker](https://docs.docker.com/get-docker/) (for RabbitMQ and PostgreSQL)

## 1. Start Infrastructure

Create a `docker-compose.yml` with RabbitMQ and PostgreSQL:

```yaml
services:
  rabbitmq:
    image: rabbitmq:4-management
    ports:
      - "5672:5672"
      - "15672:15672"
  postgres:
    image: postgres:17
    ports:
      - "5432:5432"
    environment:
      POSTGRES_USER: ratatoskr
      POSTGRES_PASSWORD: ratatoskr
      POSTGRES_DB: orders
```

Start the services:

```bash
docker compose up -d
```

## 2. Create the Project

```bash
dotnet new web -n OrderService
cd OrderService
dotnet add package Ratatoskr
dotnet add package Ratatoskr.EfCore
dotnet add package Ratatoskr.RabbitMq
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package DistributedLock.FileSystem
```

## 3. Define a Message

Create a `Messages/OrderPlaced.cs` file. The `[RatatoskrMessage]` attribute sets the CloudEvents `type` used for routing:

```csharp
using Ratatoskr;

[RatatoskrMessage("order.placed")]
public record OrderPlaced(Guid OrderId, string CustomerEmail, decimal Total);
```

## 4. Create a Handler

Create a `Handlers/OrderPlacedHandler.cs` file. Handlers implement `IMessageHandler<T>`:

```csharp
using Ratatoskr.Core;

public class OrderPlacedHandler(ILogger<OrderPlacedHandler> logger)
    : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(
        OrderPlaced message,
        MessageProperties properties,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing order {OrderId} for {Email}",
            message.OrderId, message.CustomerEmail);
        return Task.CompletedTask;
    }
}
```

## 5. Set Up the DbContext

Create a `Data/OrderDbContext.cs` file. The DbContext implements `IOutboxDbContext` to support the transactional outbox:

```csharp
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

public class OrderDbContext(DbContextOptions<OrderDbContext> options)
    : DbContext(options), IOutboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
```

## 6. Configure Connection Strings

Add connection strings to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "RabbitMq": "amqp://guest:guest@localhost:5672/",
    "OrdersDb": "Host=localhost;Database=orders;Username=ratatoskr;Password=ratatoskr"
  }
}
```

> [!WARNING]
> Never hardcode connection strings in production code. Use [user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets), environment variables, or a secrets manager.

## 7. Configure Ratatoskr

Replace the contents of `Program.cs`:

```csharp
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.EntityFrameworkCore;
using Ratatoskr;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Distributed lock provider (use database-backed provider in production)
builder.Services.AddSingleton<IDistributedLockProvider>(
    _ => new FileDistributedSynchronizationProvider(
        new DirectoryInfo(Path.GetTempPath())));

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

builder.Services.AddRatatoskr(bus =>
{
    // Configure RabbitMQ transport
    bus.UseRabbitMq(c =>
    {
        c.ConnectionString = new Uri(builder.Configuration.GetConnectionString("RabbitMq")!);
    });

    // Configure transactional outbox
    bus.AddEfCoreDurability<OrderDbContext>(d =>
    {
        d.UseOutbox();
    });

    // Publish channel — we own this exchange
    bus.AddEventPublishChannel("orders.events", c => c
        .WithRabbitMq(r => r.WithTopicExchange())
        .Produces<OrderPlaced>());

    // Consume channel — subscribe to our own events
    bus.AddEventConsumeChannel("orders.events", c => c
        .WithRabbitMq(r => r
            .WithQueueName("orders.events.handler")
            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(30)))
        .Consumes<OrderPlaced>(m => m
            .WithHandler<OrderPlacedHandler>()));
});

// Configure EF Core with PostgreSQL
builder.Services.AddDbContext<OrderDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("OrdersDb"));
    options.RegisterOutbox<OrderDbContext>(sp);
});

var app = builder.Build();

// Create database tables
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Endpoint that publishes via the outbox
app.MapPost("/orders", async (OrderDbContext db) =>
{
    var order = new OrderPlaced(Guid.NewGuid(), "customer@example.com", 99.99m);
    db.OutboxMessages.Add(order);
    await db.SaveChangesAsync();
    return TypedResults.Ok(order);
});

app.Run();
```

## 8. Run the Application

```bash
dotnet run
```

Test by sending a request:

```bash
curl -X POST http://localhost:5000/orders
```

You should see the handler log message in the console output. The message was published through the outbox (same database transaction as your business data) and consumed via RabbitMQ.

## What Happened

1. The `POST /orders` endpoint added an `OrderPlaced` message to `OutboxMessages` and called `SaveChangesAsync()`
2. The `OutboxTriggerInterceptor` serialized the message and persisted it as an `OutboxMessageEntity` in the same database transaction
3. The `OutboxProcessor` background service picked up the message and published it to the `orders.events` RabbitMQ exchange
4. The `RabbitMqConsumer` received the message from the `orders.events.handler` queue
5. The `MessageRouter` dispatched it to `OrderPlacedHandler`

> [!TIP]
> If the application crashes between steps 2 and 3, the outbox message is still safely in the database. The `OutboxProcessor` will pick it up on restart — no messages are lost.

## Alternative: EF Core Transport (No Broker)

If you don't need a message broker, you can use the EF Core transport. This delivers messages via the database — ideal for single-service applications or development environments.

Replace the transport and channel configuration:

```csharp
builder.Services.AddRatatoskr(bus =>
{
    bus.AddEfCoreDurability<OrderDbContext>(d =>
    {
        d.UseOutbox();
        d.UseInbox();
    });

    bus.AddEventPublishChannel("orders.events", c => c
        .WithEfCore()
        .Produces<OrderPlaced>());

    bus.AddEventConsumeChannel("orders.events", c => c
        .Consumes<OrderPlaced>(m => m
            .WithHandler<OrderPlacedHandler>("order-handler"))
        .UseInbox<OrderDbContext>());
});
```

The `OrderDbContext` also needs `IInboxDbContext`:

```csharp
public class OrderDbContext(DbContextOptions<OrderDbContext> options)
    : DbContext(options), IOutboxDbContext, IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
```

> [!NOTE]
> The EF Core transport requires `UseInbox()` on all consume channels and a handler key on every handler. See [EF Core Transport](efcore-transport.md) for details.

## EF Core Migrations

For production applications, use EF Core migrations instead of `EnsureCreatedAsync()`:

```bash
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet ef migrations add InitialCreate
dotnet ef database update
```

This generates migration files that can be reviewed and version-controlled.

## What's Next

- [Architecture](architecture.md) — Understand the full message flow and pipeline
- [Messages & Handlers](messages-handlers.md) — Message types, handler patterns, and serialization
- [Channels & Routing](channels-routing.md) — Channel-first design and routing rules
- [Outbox](outbox.md) — Deep dive into the transactional outbox pattern
- [RabbitMQ](rabbitmq.md) — Exchange types, topology, retry, and DLQ configuration
