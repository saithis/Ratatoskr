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
        d.UseInbox();
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
app.UseAuthentication();
app.UseAuthorization();
app.MapRatatoskrManagementApi("DevOnlyNoAuth");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapPost("/api/orders", async ([FromServices] OrdersDbContext db, [FromServices] TimeProvider time) =>
{
    var order = new Order
    {
        Id = Guid.NewGuid(),
        Status = OrderStatus.Placed,
        CreatedAt = time.GetUtcNow().UtcDateTime,
    };
    db.Orders.Add(order);
    db.OutboxMessages.Add(new OrderPlaced { OrderId = order.Id.ToString() });
    await db.SaveChangesAsync();
    return TypedResults.Ok(new { order.Id, order.Status });
});

app.MapPost("/api/orders/direct", async ([FromServices] IRatatoskr bus, [FromServices] TimeProvider time) =>
{
    var orderId = Guid.NewGuid().ToString();
    await bus.PublishDirectAsync(new OrderPlaced { OrderId = orderId });
    return TypedResults.Ok(new { orderId });
});

app.MapPost("/api/orders/{id}/replay", async (Guid id, [FromServices] OrdersDbContext db, [FromServices] IRatatoskr bus) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order is null) return Results.NotFound();
    await bus.PublishDirectAsync(new OrderPlaced { OrderId = order.Id.ToString() });
    return Results.Ok(new { order.Id });
});

app.MapGet("/api/orders", async ([FromServices] OrdersDbContext db) =>
{
    var orders = await db.Orders
        .OrderByDescending(o => o.CreatedAt)
        .Take(50)
        .Select(o => new { o.Id, o.Status, o.CreatedAt })
        .ToListAsync();
    return TypedResults.Ok(orders);
});

app.Run();
