// Local development example only. See examples/README.md.
using InventoryService;
using Microsoft.AspNetCore.Mvc;
using InventoryService.Database;
using InventoryService.Handlers;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddSingleton<FailureModeState>();

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
        d.UseInbox(o =>
            o.WithRetention(TimeSpan.FromMinutes(30))
             .WithCleanupInterval(TimeSpan.FromMinutes(5))));

    bus.AddEventPublishChannel("ecommerce.events", c => c
        .WithRabbitMq(r => r.WithTopicExchange())
        .Produces<OrderFulfilled>()
        .Produces<OrderFailed>());

    bus.AddCommandConsumeChannel("ecommerce.commands", c => c
        .WithRabbitMq(r => r
            .WithDirectExchange()
            .WithQueueName("ecommerce.commands.inventory")
            // Default is 30s. Short delay for demo visibility.
            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
        .Consumes<ProcessOrderCommand>(m => m.WithHandler<ProcessOrderHandler>(HandlerKeys.InventoryProcessOrder))
        .UseInbox<InventoryDbContext>());
});

var dbConnectionString = builder.Configuration.GetConnectionString("inventorydb")
    ?? throw new InvalidOperationException("Connection string 'inventorydb' is not configured.");

builder.Services.AddDbContext<InventoryDbContext>((sp, options) =>
{
    options.UseNpgsql(dbConnectionString);
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

// Dev-only endpoint — remove before deployment.
app.MapPost("/api/inventory/failure-mode", ([FromServices] FailureModeState state) =>
{
    var enabled = state.Toggle();
    return TypedResults.Ok(new { enabled });
});

// Dev-only endpoint — remove before deployment.
app.MapGet("/api/inventory/failure-mode", ([FromServices] FailureModeState state) =>
    TypedResults.Ok(new { enabled = state.IsEnabled }));

app.Run();
