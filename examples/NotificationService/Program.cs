// Local development example only. See examples/README.md.
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationService;
using NotificationService.Handlers;
using PlaygroundMessages;
using PlaygroundMessages.Messages;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Extensions;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<NotificationPlaygroundState>();
builder.Services.AddSingleton<PlaygroundActivityRecorder>();
builder.Services.AddSingleton<IMessageActivityObserver>(sp =>
    sp.GetRequiredService<PlaygroundActivityRecorder>());

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

    // No inbox: handler runs inline on the consumer thread, so failures use Rabbit retry + DLQ topology.
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
app.UseAuthorization();

var playground = app.MapGroup("/api/playground").RequireCors("LocalDashboard").RequireAuthorization("DevOnlyNoAuth");

playground.MapGet("/activities", ([FromServices] PlaygroundActivityRecorder recorder, Guid? orderId) =>
{
    if (orderId is { } id)
        return TypedResults.Ok(recorder.GetEntriesForOrder(id));
    return TypedResults.Ok(recorder.GetRecentEntries());
});

playground.MapGet("/control-state", ([FromServices] NotificationPlaygroundState state) =>
    TypedResults.Ok(new
    {
        service = "notificationservice",
        toggles = new object[]
        {
            new
            {
                key = "consume-orderplaced-rabbit",
                label = "Consume OrderPlaced (Rabbit, no inbox)",
                kind = "rabbitInlineHandlerFail",
                mode = state.OrderPlacedHandlerFails ? "fail" : "succeed",
            },
            new
            {
                key = "consume-orderfulfilled-rabbit",
                label = "Consume OrderFulfilled (Rabbit, no inbox)",
                kind = "rabbitInlineHandlerFail",
                mode = state.OrderFulfilledHandlerFails ? "fail" : "succeed",
            },
        },
    }));

playground.MapPost("/toggle", ([FromServices] NotificationPlaygroundState state, [FromBody] PlaygroundToggleRequest body) =>
{
    var (mode, key) = body.Key switch
    {
        "consume-orderplaced-rabbit" => (state.ToggleOrderPlacedHandlerFails() ? "fail" : "succeed", body.Key),
        "consume-orderfulfilled-rabbit" => (state.ToggleOrderFulfilledHandlerFails() ? "fail" : "succeed", body.Key),
        _ => throw new BadHttpRequestException($"Unknown toggle key '{body.Key}'."),
    };
    return TypedResults.Ok(new { key, mode });
});

app.Run();
