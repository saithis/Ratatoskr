// Local development example only. See examples/README.md.
using InventoryService;
using InventoryService.Database;
using InventoryService.Handlers;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
builder.Services.AddSingleton<InventoryDemoModeState>();
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

    bus.AddEfCoreDurability<InventoryDbContext>(d =>
    {
        d.UseOutbox(o => o.WithPollingInterval(TimeSpan.FromSeconds(2)));
        d.UseInbox(o => o
            .WithPollingInterval(TimeSpan.FromSeconds(2))
            .WithMaxRetries(5)
            .WithRetention(TimeSpan.FromMinutes(30))
            .WithCleanupInterval(TimeSpan.FromMinutes(5)));
    });

    bus.AddEventPublishChannel("ecommerce.events", c => c
        .WithRabbitMq(r => r.WithTopicExchange())
        .Produces<OrderFulfilled>()
        .Produces<OrderFailed>());

    bus.AddCommandConsumeChannel("ecommerce.commands", c => c
        .WithRabbitMq(r => r
            .WithDirectExchange()
            .WithQueueName("ecommerce.commands.inventory")
            // Retries apply when the consumer nacks before inbox acceptance; with UseInbox the handler runs in the inbox processor instead.
            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
        .Consumes<ProcessOrderCommand>(m => m.WithHandler<ProcessOrderHandler>(HandlerKeys.InventoryProcessOrder))
        .UseInbox<InventoryDbContext>());
});

var dbConnectionString = builder.Configuration.GetConnectionString("inventorydb")
    ?? throw new InvalidOperationException("Connection string 'inventorydb' is not configured.");

builder.Services.AddDbContext<InventoryDbContext>((sp, options) =>
{
    options.UseNpgsql(dbConnectionString);
    options.RegisterOutbox<InventoryDbContext>(sp);
});

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseCors("LocalDashboard");
app.UseAuthorization();
app.MapRatatoskrManagementApi("DevOnlyNoAuth");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    await db.Database.EnsureCreatedAsync();
}

var playground = app.MapGroup("/api/playground").RequireCors("LocalDashboard").RequireAuthorization("DevOnlyNoAuth");

playground.MapGet("/activities", ([FromServices] PlaygroundActivityRecorder recorder, Guid? orderId) =>
{
    if (orderId is { } id)
        return TypedResults.Ok(recorder.GetEntriesForOrder(id));
    return TypedResults.Ok(recorder.GetRecentEntries());
});

playground.MapGet("/control-state", ([FromServices] InventoryDemoModeState state) =>
    TypedResults.Ok(new
    {
        service = "inventoryservice",
        toggles = new[]
        {
            new
            {
                key = "consume-processordercommand-inbox",
                label = "Consume ProcessOrderCommand (inbox) — business path",
                kind = "inventoryCommandMode",
                mode = state.Mode.ToString().ToLowerInvariant(),
                failuresRemaining = state.Mode == InventoryDemoMode.SucceedAfter ? state.SucceedAfterFailuresRemaining : 0,
                succeedAfterBudget = state.Mode == InventoryDemoMode.SucceedAfter ? state.SucceedAfterInitialBudget : 0,
                hint = "Cycles off → throw → succeed-after(2) → reject. Or POST mode: off | throw | succeed-after | reject with failureCount.",
            },
        },
    }));

playground.MapPost("/toggle", ([FromServices] InventoryDemoModeState state, [FromBody] PlaygroundToggleRequest body) =>
{
    if (body.Key != "consume-processordercommand-inbox")
        throw new BadHttpRequestException($"Unknown toggle key '{body.Key}'.");

    if (string.IsNullOrEmpty(body.Mode))
        state.Cycle();
    else
        state.ApplyFromToggle(body.Mode, body.FailureCount);

    var mode = state.Mode;
    return TypedResults.Ok(new
    {
        key = body.Key,
        mode = mode.ToString().ToLowerInvariant(),
        enabled = mode != InventoryDemoMode.Off,
        failuresRemaining = mode == InventoryDemoMode.SucceedAfter ? state.SucceedAfterFailuresRemaining : 0,
    });
});

app.Run();
