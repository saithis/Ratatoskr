// Local development example only. See examples/README.md.
using NotificationService.Handlers;
using PlaygroundMessages;
using PlaygroundMessages.Messages;
using Ratatoskr;
using Ratatoskr.RabbitMq.Extensions;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException("Connection string 'rabbitmq' is not configured.");

builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c => { c.ConnectionString = new Uri(rabbitMqConnectionString); });

    // No inbox: NotificationService processes each message as delivered, without deduplication.
    // If OrderPlaced is replayed with the same message ID, this handler fires again — intentional
    // contrast with InventoryService, which deduplicates via inbox.
    bus.AddEventConsumeChannel("ecommerce.events", c => c
        .WithRabbitMq(r => r
            .WithTopicExchange()
            .WithQueueName("ecommerce.events.notifications"))
        .Consumes<OrderPlaced>(m => m.WithHandler<OrderPlacedNotificationHandler>(HandlerKeys.NotifyOrderPlaced))
        .Consumes<OrderFulfilled>(m => m.WithHandler<OrderFulfilledNotificationHandler>(HandlerKeys.NotifyOrderFulfilled)));
});

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
