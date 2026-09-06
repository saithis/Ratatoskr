using System.Globalization;
using InventoryService.Handlers;
using InventoryService.Messages;
using InventoryService.Persistence;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.EntityFrameworkCore;
using Ratatoskr;
using Ratatoskr.EfCore;
using Ratatoskr.Management;
using Ratatoskr.RabbitMq.Extensions;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var lockFileDirectory = new DirectoryInfo(Environment.CurrentDirectory);
builder.Services.AddSingleton<IDistributedLockProvider>(
    _ => new FileDistributedSynchronizationProvider(lockFileDirectory)
);

builder.Services.AddSingleton(TimeProvider.System);

var rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException("Connection string 'rabbitmq' is not configured.");

builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c =>
    {
        c.ConnectionString = new Uri(rabbitMqConnectionString);
    });

    bus.AddEfCoreDurability<InventoryDbContext>(d =>
    {
        d.UseOutbox(o =>
            o.WithPollingInterval(TimeSpan.FromMilliseconds(300))
                .WithMaxRetries(3)
                .WithMaxRetryDelay(TimeSpan.FromSeconds(1))
        );
        d.UseInbox(i =>
            i.WithPollingInterval(TimeSpan.FromMilliseconds(300))
                .WithMaxRetries(2)
                .WithMaxRetryDelay(TimeSpan.FromSeconds(1))
        );
    });

    bus.AddEfCoreDurability<AuditDbContext>(d =>
    {
        d.UseOutbox(o =>
            o.WithPollingInterval(TimeSpan.FromMilliseconds(300))
                .WithMaxRetries(3)
                .WithMaxRetryDelay(TimeSpan.FromSeconds(1))
        );
    });

    var queuePrefix = builder.Configuration["Inventory:QueuePrefix"] ?? "inventory";
    var commandsChannel = $"{queuePrefix}.commands";
    var auditChannel = $"{queuePrefix}.audit";

    // Command channel: reserve stock
    bus.AddCommandPublishChannel(commandsChannel, c =>
        c.WithRabbitMq(r => r.WithDirectExchange())
            .Produces<ReserveStock>()
    );
    bus.AddCommandConsumeChannel(commandsChannel, c =>
        c.WithRabbitMq(r => r.WithDirectExchange().WithQueueName($"{queuePrefix}.reserve.queue"))
            .Consumes<ReserveStock>(m => m.WithHandler<ReserveStockHandler>("inventory.reserve-stock"))
            .UseInbox<InventoryDbContext>()
    );

    // Event channel: stock audited
    bus.AddEventPublishChannel(auditChannel, c =>
        c.WithRabbitMq(r => r.WithTopicExchange())
            .Produces<StockAudited>()
    );
    bus.AddEventConsumeChannel(auditChannel, c =>
        c.WithRabbitMq(r => r.WithTopicExchange().WithQueueName($"{queuePrefix}.audit.queue"))
            .Consumes<StockAudited>(m => m.WithHandler<StockAuditedHandler>("inventory.stock-audited"))
            .UseInbox<InventoryDbContext>()
    );
});

// Configure Ratatoskr Management Agent for this microservice
builder.Services.AddRatatoskrManagement(options =>
{
    options.ServiceName = builder.Configuration["Ratatoskr:Management:ServiceName"] ?? "inventory-service";
    options.InstanceId = $"{Environment.MachineName}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";
    options.UiExchangePrefix = builder.Configuration["Ratatoskr:Management:UiExchangePrefix"] ?? "ratatoskr.ui";
    options.HeartbeatInterval = TimeSpan.FromSeconds(5);
    options.EnableHeartbeat = true;
});

var inventoryCs =
    builder.Configuration.GetConnectionString("inventorydb")
    ?? throw new InvalidOperationException("Connection string 'inventorydb' is not configured.");
var auditCs =
    builder.Configuration.GetConnectionString("auditdb")
    ?? throw new InvalidOperationException("Connection string 'auditdb' is not configured.");

builder.Services.AddDbContext<InventoryDbContext>(
    (sp, options) =>
    {
        options.UseNpgsql(inventoryCs);
        options.RegisterOutbox<InventoryDbContext>(sp);
    }
);

builder.Services.AddDbContext<AuditDbContext>(
    (sp, options) =>
    {
        options.UseNpgsql(auditCs);
        options.RegisterOutbox<AuditDbContext>(sp);
    }
);

var app = builder.Build();

app.MapDefaultEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.EnsureCreatedAsync();
    await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.EnsureCreatedAsync();
}

app.MapPost(
    "/inventory/reservations",
    async (
        ReserveStock request,
        InventoryDbContext db,
        CancellationToken cancellationToken
    ) =>
    {
        db.OutboxMessages.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted("/inventory/reservations", request);
    }
);

app.MapPost(
    "/inventory/reservations/simulate-failure",
    async (
        InventoryDbContext db,
        CancellationToken cancellationToken
    ) =>
    {
        var failingSku = $"FAIL-WIDGET-{Guid.NewGuid().ToString("N")[..6]}";
        var command = new ReserveStock(failingSku, 1);
        db.OutboxMessages.Add(command);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted(
            "/inventory/reservations",
            new
            {
                sku = failingSku,
                quantity = 1,
                message = "Failing reservation dispatched. It will exhaust inbox retries and appear as Poisoned in the Ratatoskr UI.",
            }
        );
    }
);

app.MapGet(
    "/inventory/reservations",
    async (InventoryDbContext db, CancellationToken cancellationToken) =>
    {
        var reservations = await db
            .Reservations.OrderByDescending(r => r.ReservedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(reservations);
    }
);

await app.RunAsync();
