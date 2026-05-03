// Local development example only. See examples/README.md.
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderService.Database;
using OrderService.Database.Entities;
using OrderService.Handlers;
using PlaygroundMessages;
using PlaygroundMessages.Messages;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.Management;
using Ratatoskr.RabbitMq.Extensions;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var lockFileDirectory = new DirectoryInfo(Environment.CurrentDirectory);
builder.Services.AddSingleton<IDistributedLockProvider>(_ => new FileDistributedSynchronizationProvider(lockFileDirectory));

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

// DevOnlyNoAuth: permissive policy for local development example only.
// Remove or replace before any deployment.
builder.Services.AddAuthorization(o =>
    o.AddPolicy("DevOnlyNoAuth", p => p.RequireAssertion(_ => true)));

// AllowAnyOrigin is intentional for a local dev example only.
// Gate behind app.Environment.IsDevelopment() before any deployment.
builder.Services.AddCors(o => o.AddPolicy("LocalDashboard",
    p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException("Connection string 'rabbitmq' is not configured.");

builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c => { c.ConnectionString = new Uri(rabbitMqConnectionString); });

    bus.AddEfCoreDurability<OrdersDbContext>(d =>
    {
        // Default polling is 60s. Short interval so crash-recovery is visible in demos.
        d.UseOutbox(o => o.WithPollingInterval(TimeSpan.FromSeconds(2)));
        d.UseInbox(i => i.WithPollingInterval(TimeSpan.FromSeconds(2)));
    });

    bus.AddEventPublishChannel("ecommerce.events", c => c
        .WithRabbitMq(r => r.WithTopicExchange())
        .Produces<OrderPlaced>());

    bus.AddCommandPublishChannel("ecommerce.commands", c => c
        .WithRabbitMq(r => r.WithDirectExchange())
        .Produces<ProcessOrderCommand>());

    bus.AddEventConsumeChannel("ecommerce.events", c => c
        .WithRabbitMq(r => r
            .WithTopicExchange()
            .WithQueueName("ecommerce.events.orders"))
        .Consumes<OrderFulfilled>(m => m.WithHandler<OrderFulfilledHandler>(HandlerKeys.OrderFulfilled))
        .Consumes<OrderFailed>(m => m.WithHandler<OrderFailedHandler>(HandlerKeys.OrderFailed))
        .UseInbox<OrdersDbContext>());
});

var dbConnectionString = builder.Configuration.GetConnectionString("ordersdb")
    ?? throw new InvalidOperationException("Connection string 'ordersdb' is not configured.");

builder.Services.AddDbContext<OrdersDbContext>((sp, options) =>
{
    options.UseNpgsql(dbConnectionString);
    options.RegisterOutbox<OrdersDbContext>(sp);
});

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseCors("LocalDashboard");
app.UseAuthorization();
app.MapRatatoskrManagementApi("DevOnlyNoAuth");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapPost("/api/orders", async ([FromServices] OrdersDbContext db, [FromServices] TimeProvider time) =>
{
    var now = time.GetUtcNow().UtcDateTime;
    var order = new Order
    {
        Id = Guid.NewGuid(),
        Status = OrderStatus.Placed,
        CreatedAt = now,
        StatusChangedAt = now,
    };
    db.Orders.Add(order);
    var orderIdStr = order.Id.ToString();
    db.OutboxMessages.Add(
        new OrderPlaced { OrderId = orderIdStr },
        new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) });
    db.OutboxMessages.Add(
        new ProcessOrderCommand { OrderId = orderIdStr },
        new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) });
    await db.SaveChangesAsync();
    return TypedResults.Ok(new { order.Id, order.Status });
});

app.MapPost("/api/orders/direct", async ([FromServices] IRatatoskr bus) =>
{
    var orderGuid = Guid.NewGuid();
    var orderId = orderGuid.ToString();
    await bus.PublishDirectAsync(
        new OrderPlaced { OrderId = orderId },
        new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(orderGuid) });
    await bus.PublishDirectAsync(
        new ProcessOrderCommand { OrderId = orderId },
        new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(orderGuid) });
    return TypedResults.Ok(new { orderId });
});

app.MapPost("/api/orders/{id}/replay", async (Guid id, [FromServices] OrdersDbContext db, [FromServices] IRatatoskr bus) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order is null) return Results.NotFound();
    var orderIdStr = order.Id.ToString();
    await bus.PublishDirectAsync(
        new OrderPlaced { OrderId = orderIdStr },
        new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) });
    await bus.PublishDirectAsync(
        new ProcessOrderCommand { OrderId = orderIdStr },
        new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) });
    return Results.Ok(new { order.Id });
});

app.MapGet("/api/orders", async ([FromServices] OrdersDbContext db) =>
{
    var orders = await db.Orders
        .OrderByDescending(o => o.CreatedAt)
        .Take(50)
        .Select(o => new { o.Id, o.Status, o.CreatedAt, o.StatusChangedAt })
        .ToListAsync();
    return TypedResults.Ok(orders);
});

app.MapGet("/api/orders/{id:guid}/flow", async (Guid id, [FromServices] OrdersDbContext db) =>
{
    var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
    if (order is null) return Results.NotFound();

    var terminal = order.Status is OrderStatus.Fulfilled or OrderStatus.Failed;
    var flow = new
    {
        order.Id,
        status = order.Status.ToString(),
        order.CreatedAt,
        order.StatusChangedAt,
        messageIds = new
        {
            orderPlaced = PlaygroundMessageIds.OrderPlaced(order.Id),
            processOrderCommand = PlaygroundMessageIds.ProcessOrderCommand(order.Id),
        },
        steps = new[]
        {
            new { key = "persisted", label = "Order row created", done = true },
            new { key = "inventory", label = "Inventory processed command", done = terminal },
            new
            {
                key = "terminal",
                label = order.Status switch
                {
                    OrderStatus.Fulfilled => "Fulfilled",
                    OrderStatus.Failed => "Failed",
                    _ => "Awaiting fulfillment or failure",
                },
                done = terminal,
            },
        },
    };
    return TypedResults.Ok(flow);
});

app.Run();
