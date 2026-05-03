// Local development example only. See examples/README.md.
using Microsoft.AspNetCore.Mvc;
using NotificationService;
using NotificationService.Handlers;
using PlaygroundMessages;
using PlaygroundMessages.Messages;
using Ratatoskr;
using Ratatoskr.RabbitMq.Extensions;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<NotificationFailureState>();

// AllowAnyOrigin is intentional for a local dev example only.
builder.Services.AddCors(o => o.AddPolicy("LocalDashboard",
    p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException("Connection string 'rabbitmq' is not configured.");

builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c => { c.ConnectionString = new Uri(rabbitMqConnectionString); });

    // No inbox: handler runs inline on the consumer thread, so failures use Rabbit retry + DLQ topology.
    // If OrderPlaced is replayed with the same message ID, this handler fires again — intentional
    // contrast with InventoryService, which deduplicates via inbox.
    bus.AddEventConsumeChannel("ecommerce.events", c => c
        .WithRabbitMq(r => r
            .WithTopicExchange()
            .WithQueueName("ecommerce.events.notifications")
            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
        .Consumes<OrderPlaced>(m => m.WithHandler<OrderPlacedNotificationHandler>(HandlerKeys.NotifyOrderPlaced))
        .Consumes<OrderFulfilled>(m => m.WithHandler<OrderFulfilledNotificationHandler>(HandlerKeys.NotifyOrderFulfilled)));
});

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseCors("LocalDashboard");

// Dev-only endpoints — remove before deployment.
app.MapPost("/api/notifications/failure-mode", ([FromServices] NotificationFailureState state) =>
{
    var enabled = state.Toggle();
    return TypedResults.Ok(new { enabled });
});

app.MapGet("/api/notifications/failure-mode", ([FromServices] NotificationFailureState state) =>
    TypedResults.Ok(new { enabled = state.IsEnabled }));

app.Run();
