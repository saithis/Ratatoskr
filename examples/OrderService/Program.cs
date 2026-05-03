// Local development example only. See examples/README.md.
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderService.Database;
using OrderService.Database.Entities;
using OrderService.Handlers;
using OrderService.Playground;
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
builder.Services.AddSingleton<OrderConsumePlaygroundState>();
builder.Services.AddSingleton<PlaygroundActivityRecorder>();
builder.Services.AddSingleton<IMessageActivityObserver>(sp =>
    sp.GetRequiredService<PlaygroundActivityRecorder>());

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
            .WithQueueName("ecommerce.events.orders")
            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
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
        PublishOrigin = "outbox",
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

app.MapPost("/api/orders/direct", async ([FromServices] OrdersDbContext db, [FromServices] IRatatoskr bus, [FromServices] TimeProvider time) =>
{
    var now = time.GetUtcNow().UtcDateTime;
    var order = new Order
    {
        Id = Guid.NewGuid(),
        Status = OrderStatus.Placed,
        CreatedAt = now,
        StatusChangedAt = now,
        PublishOrigin = "direct",
    };
    db.Orders.Add(order);
    await db.SaveChangesAsync();
    var orderIdStr = order.Id.ToString();
    await bus.PublishDirectAsync(
        new OrderPlaced { OrderId = orderIdStr },
        new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) });
    await bus.PublishDirectAsync(
        new ProcessOrderCommand { OrderId = orderIdStr },
        new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) });
    return TypedResults.Ok(new { id = order.Id, status = order.Status.ToString() });
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
        .Select(o => new { o.Id, o.Status, o.CreatedAt, o.StatusChangedAt, o.PublishOrigin })
        .ToListAsync();
    return TypedResults.Ok(orders);
});

app.MapGet("/api/orders/{id:guid}/flow", async (Guid id, [FromServices] OrdersDbContext db) =>
{
    var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
    if (order is null) return Results.NotFound();

    var terminal = order.Status is OrderStatus.Fulfilled or OrderStatus.Failed;
    var publishOrigin = string.IsNullOrEmpty(order.PublishOrigin) ? "outbox" : order.PublishOrigin;
    var flow = new
    {
        order.Id,
        status = order.Status.ToString(),
        publishOrigin,
        order.CreatedAt,
        order.StatusChangedAt,
        messageIds = new
        {
            orderPlaced = PlaygroundMessageIds.OrderPlaced(order.Id),
            processOrderCommand = PlaygroundMessageIds.ProcessOrderCommand(order.Id),
            orderFulfilled = PlaygroundMessageIds.OrderFulfilled(order.Id),
            orderFailed = PlaygroundMessageIds.OrderFailed(order.Id),
        },
        steps = new object[]
        {
            new { key = "persisted", label = "Order row created", done = true },
            new
            {
                key = "initial-publish",
                label = publishOrigin == "direct"
                    ? "OrderPlaced + ProcessOrderCommand published direct (no outbox)"
                    : "OrderPlaced + ProcessOrderCommand staged in OrderService outbox",
                done = true,
            },
            new
            {
                key = "outbox-relay",
                label = "OrderService outbox relayed both messages to Rabbit",
                done = publishOrigin == "direct",
            },
            new { key = "inventory", label = "Inventory consumed command (inbox) and published outcome via outbox", done = terminal },
            new
            {
                key = "terminal",
                label = order.Status switch
                {
                    OrderStatus.Fulfilled => "OrderService consumed OrderFulfilled (inbox)",
                    OrderStatus.Failed => "OrderService consumed OrderFailed (inbox)",
                    _ => "Awaiting OrderFulfilled or OrderFailed on OrderService inbox",
                },
                done = terminal,
            },
        },
    };
    return TypedResults.Ok(flow);
});

var playground = app.MapGroup("/api/playground").RequireCors("LocalDashboard").RequireAuthorization("DevOnlyNoAuth");

playground.MapGet("/activities", ([FromServices] PlaygroundActivityRecorder recorder, Guid? orderId) =>
{
    if (orderId is { } id)
        return TypedResults.Ok(recorder.GetEntriesForOrder(id));
    return TypedResults.Ok(recorder.GetRecentEntries());
});

playground.MapGet("/control-state", ([FromServices] OrderConsumePlaygroundState state) =>
    TypedResults.Ok(new
    {
        service = "orderservice",
        toggles =
            new[]
            {
                new
                {
                    key = "consume-orderfulfilled-inbox",
                    label = "Consume OrderFulfilled (inbox)",
                    kind = "inboxHandlerFail",
                    mode = state.OrderFulfilledHandlerFails ? "fail" : "succeed",
                },
                new
                {
                    key = "consume-orderfailed-inbox",
                    label = "Consume OrderFailed (inbox)",
                    kind = "inboxHandlerFail",
                    mode = state.OrderFailedHandlerFails ? "fail" : "succeed",
                },
            },
    }));

playground.MapPost("/toggle", ([FromServices] OrderConsumePlaygroundState state, [FromBody] PlaygroundToggleRequest body) =>
{
    var next = body.Key switch
    {
        "consume-orderfulfilled-inbox" => state.ToggleOrderFulfilledFails() ? "fail" : "succeed",
        "consume-orderfailed-inbox" => state.ToggleOrderFailedFails() ? "fail" : "succeed",
        _ => throw new BadHttpRequestException($"Unknown toggle key '{body.Key}'."),
    };
    return TypedResults.Ok(new { key = body.Key, mode = next });
});

app.Run();
