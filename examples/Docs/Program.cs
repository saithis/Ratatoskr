using Docs.Data;
using Docs.Handlers;
using Docs.Messages;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Ratatoskr;
using Ratatoskr.AsyncApi.Extensions;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Extensions;

var builder = WebApplication.CreateBuilder(args);

#region DistributedLock
builder.Services.AddSingleton<IDistributedLockProvider>(
    _ => new FileDistributedSynchronizationProvider(new DirectoryInfo(Path.GetTempPath()))
);
#endregion

builder.Services.AddSingleton(TimeProvider.System);

#region AddRatatoskr
builder.Services.AddRatatoskr(bus =>
{
    #region ConfigureRabbitMq
    bus.UseRabbitMq(c =>
    {
        c.ConnectionString = new Uri(builder.Configuration.GetConnectionString("RabbitMq")!);
    });
    #endregion

    #region ConfigureDurability
    bus.AddEfCoreDurability<OrderDbContext>(d =>
    {
        d.UseOutbox();
        d.UseInbox();
    });
    #endregion

    #region ConfigurePublishChannels
    bus.AddEventPublishChannel(
        "orders.events",
        c =>
            c.WithRabbitMq(r => r.WithTopicExchange())
                .Produces<OrderPlaced>()
                .Produces<PaymentCompleted>()
                .Produces<OrderShipped>()
    );

    bus.AddCommandPublishChannel(
        "orders.commands",
        c =>
            c.WithRabbitMq(r => r.WithDirectExchange())
                .Produces<ProcessPayment>()
                .Produces<ShipOrder>()
                .Produces<SendOrderConfirmation>()
    );
    #endregion

    #region ConfigureConsumeChannels
    bus.AddEventConsumeChannel(
        "orders.events",
        c =>
            c.WithRabbitMq(r =>
                    r.WithQueueName("orders.events.subscriptions")
                        .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(30))
                )
                .Consumes<OrderPlaced>(m => m.WithHandler<OrderPlacedHandler>("order-placed"))
                .UseInbox<OrderDbContext>()
    );

    bus.AddCommandConsumeChannel(
        "orders.commands",
        c =>
            c.WithRabbitMq(r => r.WithDirectExchange().WithQueueName("orders.commands.queue"))
                .Consumes<ProcessPayment>(m =>
                    m.WithHandler<ProcessPaymentHandler>("process-payment")
                )
                .Consumes<ShipOrder>(m => m.WithHandler<ShipOrderHandler>("ship-order"))
                .Consumes<SendOrderConfirmation>(m =>
                    m.WithHandler<SendOrderConfirmationHandler>("send-confirmation")
                )
                .UseInbox<OrderDbContext>()
    );
    #endregion

    #region ConfigureAsyncApi
    bus.ConfigureAsyncApi(api =>
    {
        api.WithTitle("Order Processing API");
        api.WithVersion("1.0.0");
        api.WithDescription("Ratatoskr-powered order processing messaging API");
    });
    #endregion

    #region ConfigureCloudEvents
    bus.ConfigureCloudEvents(ce =>
    {
        ce.DefaultSource = "/order-service";
    });
    #endregion
});
#endregion

#region ConfigureDbContext
var ordersConnectionString =
    builder.Configuration.GetConnectionString("OrdersDb")
    ?? throw new InvalidOperationException("Connection string 'OrdersDb' is not configured.");

builder.Services.AddDbContext<OrderDbContext>(
    (sp, options) =>
    {
        options.UseNpgsql(ordersConnectionString);
        options.RegisterOutbox<OrderDbContext>(sp);
    }
);
#endregion

#region ConfigureOpenTelemetry
builder
    .Services.AddOpenTelemetry()
    .WithTracing(tracing =>
        tracing.AddSource(RatatoskrDiagnostics.ActivitySourceName).AddAspNetCoreInstrumentation()
    )
    .WithMetrics(metrics =>
        metrics.AddMeter(RatatoskrDiagnostics.MeterName).AddAspNetCoreInstrumentation()
    );
#endregion

var app = builder.Build();

#region EnsureDatabase
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.EnsureCreatedAsync();
}
#endregion

#region PublishDirectExample
app.MapPost(
    "/orders/direct",
    async (OrderPlaced order, IRatatoskr bus) =>
    {
        await bus.PublishDirectAsync(order);
        return TypedResults.Ok(order);
    }
);
#endregion

#region PublishOutboxExample
app.MapPost(
    "/orders",
    async (OrderPlaced order, OrderDbContext db) =>
    {
        db.OutboxMessages.Add(order);
        await db.SaveChangesAsync();
        return TypedResults.Ok(order);
    }
);
#endregion

#region MapAsyncApi
app.MapAsyncApi();
#endregion

app.Run();
