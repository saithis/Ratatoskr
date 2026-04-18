using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderService;
using OrderService.Data;
using OrderService.Messages;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.Management;
using Ratatoskr.RabbitMq.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

var lockDir = new DirectoryInfo(Environment.CurrentDirectory);
builder.Services.AddSingleton<IDistributedLockProvider>(
    _ => new FileDistributedSynchronizationProvider(lockDir));

var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq");
builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c => { c.ConnectionString = new Uri(rabbitMqConnectionString!); });

    bus.AddEfCoreDurability<OrdersDbContext>(d => d.UseInbox().UseOutbox());

    bus.AddEventPublishChannel("orders.topic", c => c
        .WithRabbitMq(r => r.WithTopicExchange())
        .Produces<OrderCreatedEvent>());

    bus.AddEventConsumeChannel("orders.topic", c => c
        .WithRabbitMq(r => r
            .WithTopicExchange()
            .WithQueueName("orders.subscriptions"))
        .Consumes<OrderCreatedEvent>(m => m.WithHandler<OrderCreatedEventHandler>()));
});

var dbConnectionString = builder.Configuration.GetConnectionString("ordersdb");
if (!string.IsNullOrEmpty(dbConnectionString))
{
    builder.Services.AddDbContext<OrdersDbContext>((sp, options) =>
    {
        options.UseNpgsql(dbConnectionString);
        options.RegisterOutbox<OrdersDbContext>(sp);
    });
}
else
{
    builder.Services.AddDbContext<OrdersDbContext>((sp, opts) =>
        opts.UseInMemoryDatabase("ordersDb").RegisterOutbox<OrdersDbContext>(sp));
}

// Open authorization for the management API in this example.
// In production, replace with a real policy (JWT, API key, etc.).
builder.Services.AddAuthentication();
builder.Services.AddAuthorization(o =>
    o.AddPolicy("RatatoskrAdmin", p => p.RequireAssertion(_ => true)));

var app = builder.Build();

if (!string.IsNullOrEmpty(dbConnectionString))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "OrderService running");

// POST /orders — publish an OrderCreatedEvent via the outbox
app.MapPost("/orders", async (
    [FromBody] CreateOrderRequest req,
    [FromServices] OrdersDbContext db,
    [FromServices] TimeProvider time) =>
{
    var evt = new OrderCreatedEvent
    {
        OrderId = Guid.NewGuid(),
        CustomerName = req.CustomerName,
        TotalAmount = req.TotalAmount,
        CreatedAt = time.GetUtcNow(),
    };
    db.OutboxMessages.Add(evt);
    await db.SaveChangesAsync();
    return TypedResults.Created($"/orders/{evt.OrderId}", evt);
});

app.MapRatatoskrManagementApi("RatatoskrAdmin");

app.Run();

namespace OrderService
{
    public record CreateOrderRequest(string CustomerName, decimal TotalAmount);

    public class OrderCreatedEventHandler(ILogger<OrderCreatedEventHandler> logger)
        : IMessageHandler<Messages.OrderCreatedEvent>
    {
        public Task HandleAsync(Messages.OrderCreatedEvent message, MessageProperties properties,
            CancellationToken cancellationToken)
        {
            logger.LogInformation("Order received: {OrderId} for {Customer}",
                message.OrderId, message.CustomerName);
            return Task.CompletedTask;
        }
    }
}
