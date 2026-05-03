// Local development example only. See examples/README.md.
using Microsoft.AspNetCore.Http;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using PlaygroundHost.Scenarios.DemoOrderPipeline;
using PlaygroundHost.Scenarios.DemoOrderPipeline.Handlers;
using PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;
using RabbitMQ.Client;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.Management;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<PlaygroundOptions>(builder.Configuration.GetSection(PlaygroundOptions.SectionName));
builder.Services.PostConfigure<PlaygroundOptions>(o =>
{
    if (string.Equals(Environment.GetEnvironmentVariable("RATATOSKR_EXAMPLES_PLAYGROUND"), "1", StringComparison.Ordinal))
        o.Enabled = true;
});

var lockFileDirectory = new DirectoryInfo(Environment.CurrentDirectory);
builder.Services.AddSingleton<IDistributedLockProvider>(_ => new FileDistributedSynchronizationProvider(lockFileDirectory));

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<OutboxFailureState>();
builder.Services.AddSingleton<OrderConsumePlaygroundState>();
builder.Services.AddSingleton<InventoryDemoModeState>();
builder.Services.AddSingleton<NotificationPlaygroundState>();
builder.Services.AddSingleton<PlaygroundActivityRecorder>();
builder.Services.AddSingleton<IMessageActivityObserver>(sp =>
    sp.GetRequiredService<PlaygroundActivityRecorder>());

builder.Services.AddAuthorization(o =>
    o.AddPolicy("DevOnlyNoAuth", p => p.RequireAssertion(_ => true)));

builder.Services.AddCors(o => o.AddPolicy("LocalDashboard",
    p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException("Connection string 'rabbitmq' is not configured.");

builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c => { c.ConnectionString = new Uri(rabbitMqConnectionString); });

    bus.AddEfCoreDurability<PublisherDbContext>(d =>
    {
        d.UseOutbox(o => o
            .WithPollingInterval(TimeSpan.FromSeconds(2))
            .WithMaxMessageSize(8_192));
        d.UseInbox(i => i.WithPollingInterval(TimeSpan.FromSeconds(2)));
    });

    bus.AddEfCoreDurability<ConsumerDbContext>(d =>
    {
        d.UseOutbox(o => o.WithPollingInterval(TimeSpan.FromSeconds(2)));
        d.UseInbox(o => o
            .WithPollingInterval(TimeSpan.FromSeconds(2))
            .WithMaxRetries(5)
            .WithRetention(TimeSpan.FromMinutes(30))
            .WithCleanupInterval(TimeSpan.FromMinutes(5)));
    });

    bus.AddCommandPublishChannel("orders.internal", c => c
        .WithEfCore()
        .Produces<ReserveStockInternal>());

    bus.AddCommandConsumeChannel("orders.internal", c => c
        .Consumes<ReserveStockInternal>(m => m.WithHandler<ReserveStockInternalHandler>(HandlerKeys.ReserveStockInternal))
        .UseInbox<PublisherDbContext>());

    bus.AddEventPublishChannel("ecommerce.events", c => c
        .WithRabbitMq(r => r.WithTopicExchange())
        .Produces<OrderPlaced>()
        .Produces<OrderFulfilled>()
        .Produces<OrderFailed>());

    bus.AddCommandPublishChannel("ecommerce.commands", c => c
        .WithRabbitMq(r => r.WithDirectExchange())
        .Produces<ProcessOrderCommand>());

    bus.AddEventConsumeChannel("ecommerce.events.orders", c => c
        .WithRabbitMq(r => r
            .WithTopicExchange()
            .WithAmqpExchangeName("ecommerce.events")
            .WithQueueName("ecommerce.events.orders")
            .WithQueueType(QueueType.Classic)
            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
        .Consumes<OrderFulfilled>(m => m.WithHandler<OrderFulfilledHandler>(HandlerKeys.OrderFulfilled))
        .Consumes<OrderFailed>(m => m.WithHandler<OrderFailedHandler>(HandlerKeys.OrderFailed))
        .UseInbox<PublisherDbContext>());

    bus.AddCommandConsumeChannel("ecommerce.commands", c => c
        .WithRabbitMq(r => r
            .WithDirectExchange()
            .WithQueueName("ecommerce.commands.inventory")
            .WithQueueType(QueueType.Classic)
            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
        .Consumes<ProcessOrderCommand>(m => m.WithHandler<ProcessOrderHandler>(HandlerKeys.InventoryProcessOrder))
        .UseInbox<ConsumerDbContext>());

    bus.AddEventConsumeChannel("ecommerce.events.notifications", c => c
        .WithRabbitMq(r => r
            .WithTopicExchange()
            .WithAmqpExchangeName("ecommerce.events")
            .WithQueueName("ecommerce.events.notifications")
            .WithQueueType(QueueType.Classic)
            .WithRetry(maxRetries: 3, delay: TimeSpan.FromSeconds(5)))
        .Consumes<OrderPlaced>(m => m
            .WithHandler<OrderPlacedNotificationHandler>()
            .WithHandler<OrderPlacedAnalyticsHandler>())
        .Consumes<OrderFulfilled>(m => m.WithHandler<OrderFulfilledNotificationHandler>()));
});

PlaygroundMessageSenderDecoration.WrapAllMessageSenders(builder.Services);

var publisherCs = builder.Configuration.GetConnectionString("publisherdb")
    ?? throw new InvalidOperationException("Connection string 'publisherdb' is not configured.");
var consumerCs = builder.Configuration.GetConnectionString("consumerdb")
    ?? throw new InvalidOperationException("Connection string 'consumerdb' is not configured.");
var playgroundCs = builder.Configuration.GetConnectionString("playgrounddb")
    ?? throw new InvalidOperationException("Connection string 'playgrounddb' is not configured.");

builder.Services.AddDbContext<PublisherDbContext>((sp, options) =>
{
    options.UseNpgsql(publisherCs);
    options.RegisterOutbox<PublisherDbContext>(sp);
});

builder.Services.AddDbContext<ConsumerDbContext>((sp, options) =>
{
    options.UseNpgsql(consumerCs);
    options.RegisterOutbox<ConsumerDbContext>(sp);
});

builder.Services.AddDbContext<PlaygroundDbContext>(options => options.UseNpgsql(playgroundCs));

builder.Services.AddSingleton<IScenario, OutboxSuccessScenario>();
builder.Services.AddSingleton<IScenario, OutboxRetryThenSuccessScenario>();
builder.Services.AddSingleton<IScenario, OutboxPoisonScenario>();
builder.Services.AddSingleton<IScenario, InboxRetryThenSuccessScenario>();
builder.Services.AddSingleton<IScenario, InboxPoisonScenario>();
builder.Services.AddSingleton<IScenario, BusinessRejectionScenario>();
builder.Services.AddSingleton<IScenario, DirectPublishSuccessScenario>();
builder.Services.AddSingleton<IScenario, DirectConsumeRetryScenario>();
builder.Services.AddSingleton<IScenario, DirectConsumeDlqScenario>();
builder.Services.AddSingleton<IScenario, OversizedPayloadScenario>();
builder.Services.AddSingleton<IScenario, FanoutTwoHandlersScenario>();
builder.Services.AddSingleton<IScenario, EfCoreInternalCommandScenario>();
builder.Services.AddSingleton<IScenario, ReplayDedupScenario>();
if (string.Equals(builder.Configuration["Playground:RegisterBlockingScenario"], "1", StringComparison.Ordinal))
    builder.Services.AddSingleton<IScenario, BlockingHoldScenario>();
if (string.Equals(builder.Configuration["Playground:RegisterCancelSmokeScenario"], "1", StringComparison.Ordinal))
    builder.Services.AddSingleton<IScenario, CancelSmokeScenario>();
builder.Services.AddSingleton<ScenarioRunService>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseCors("LocalDashboard");
app.UseAuthorization();

app.MapGet("/api/config", (IConfiguration config) =>
{
    var self = config["Self:PublicBaseUrl"] ?? "";
    var rabbitConfigured = !string.IsNullOrEmpty(config.GetConnectionString("rabbitmq"));
    const string mgmtBase = "ratatoskr/api/v1/efcore/contexts";
    return TypedResults.Ok(new
    {
        playgroundHostUrl = string.IsNullOrEmpty(self) ? (string?)null : self.TrimEnd('/'),
        managementBasePath = mgmtBase,
        publisherManagementPath = $"{mgmtBase}/PublisherDbContext",
        consumerManagementPath = $"{mgmtBase}/ConsumerDbContext",
        rabbitConfigured,
        stores = new[]
        {
            new { key = "publisher", contextName = "PublisherDbContext", role = "Outbox + inbox (publisher-side orders)" },
            new { key = "consumer", contextName = "ConsumerDbContext", role = "Command inbox + outcome outbox" },
        },
    });
});

app.MapGet("/api/playground/rabbit-depths", async Task<IResult> (HttpContext http) =>
{
    if (!http.RequestServices.GetRequiredService<IOptions<PlaygroundOptions>>().Value.Enabled)
        return TypedResults.NotFound();
    var cancellationToken = http.RequestAborted;
    var config = http.RequestServices.GetRequiredService<IConfiguration>();
    var cs = config.GetConnectionString("rabbitmq");
    if (string.IsNullOrEmpty(cs))
        return TypedResults.Ok(new { configured = false, queues = Array.Empty<object>(), note = "rabbitmq connection string missing." });

    var factory = new ConnectionFactory { Uri = new Uri(cs) };
    await using var connection = await factory.CreateConnectionAsync(cancellationToken);
    await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

    var queues = new List<object>();
    foreach (var q in PlaygroundRabbitQueues.ConsumerQueues)
    {
        var main = await SafeMessageCountAsync(channel, q.MainQueueName, cancellationToken);
        var retry = await SafeMessageCountAsync(channel, PlaygroundRabbitQueues.RetryQueueName(q.MainQueueName), cancellationToken);
        var dlq = await SafeMessageCountAsync(channel, PlaygroundRabbitQueues.DlqQueueName(q.MainQueueName), cancellationToken);
        queues.Add(new
        {
            q.Key,
            mainQueue = q.MainQueueName,
            main,
            retry,
            dlq,
            retryDelaySeconds = q.RetryDelaySeconds,
        });
    }

    return TypedResults.Ok(new { configured = true, queues });
}).RequireCors("LocalDashboard");

app.MapRatatoskrManagementApi("DevOnlyNoAuth");

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<PublisherDbContext>().Database.EnsureCreatedAsync();
    await scope.ServiceProvider.GetRequiredService<ConsumerDbContext>().Database.EnsureCreatedAsync();
    await scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>().Database.EnsureCreatedAsync();
}

app.MapPost("/api/orders", async ([FromServices] PublisherDbContext db, [FromServices] TimeProvider time) =>
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
    var p1 = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
    var p2 = new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) };
    var p3 = new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(order.Id) };
    db.OutboxMessages.Add(new OrderPlaced { OrderId = orderIdStr }, p1);
    db.OutboxMessages.Add(new ProcessOrderCommand { OrderId = orderIdStr }, p2);
    db.OutboxMessages.Add(new ReserveStockInternal { OrderId = orderIdStr }, p3);
    await db.SaveChangesAsync();
    return TypedResults.Ok(new { order.Id, order.Status });
});

app.MapPost("/api/orders/direct", async ([FromServices] PublisherDbContext db, [FromServices] IRatatoskr bus, [FromServices] TimeProvider time) =>
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
    await bus.PublishDirectAsync(
        new ReserveStockInternal { OrderId = orderIdStr },
        new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(order.Id) });
    return TypedResults.Ok(new { id = order.Id, status = order.Status.ToString() });
});

app.MapPost("/api/orders/{id}/replay", async (Guid id, [FromServices] PublisherDbContext db, [FromServices] IRatatoskr bus) =>
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
    await bus.PublishDirectAsync(
        new ReserveStockInternal { OrderId = orderIdStr },
        new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(order.Id) });
    return Results.Ok(new { order.Id });
});

app.MapGet("/api/orders", async ([FromServices] PublisherDbContext db) =>
{
    var orders = await db.Orders
        .OrderByDescending(o => o.CreatedAt)
        .Take(50)
        .Select(o => new { o.Id, o.Status, o.CreatedAt, o.StatusChangedAt, o.PublishOrigin })
        .ToListAsync();
    return TypedResults.Ok(orders);
});

app.MapGet("/api/orders/{id:guid}/flow", async (Guid id, [FromServices] PublisherDbContext db) =>
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
            reserveStockInternal = PlaygroundMessageIds.ReserveStockInternal(order.Id),
            orderFulfilled = PlaygroundMessageIds.OrderFulfilled(order.Id),
            orderFailed = PlaygroundMessageIds.OrderFailed(order.Id),
        },
        steps = new object[]
        {
            new { key = "persisted", label = "Order row created", done = true },
            new
            {
                key = "internal-reserve",
                label = publishOrigin == "direct"
                    ? "ReserveStockInternal published via EF Core transport (inbox)"
                    : "ReserveStockInternal staged for EF Core inbox (same SaveChanges / same DbContext)",
                done = true,
            },
            new
            {
                key = "initial-publish",
                label = publishOrigin == "direct"
                    ? "OrderPlaced + ProcessOrderCommand published direct (no outbox)"
                    : "OrderPlaced + ProcessOrderCommand staged in publisher outbox",
                done = true,
            },
            new
            {
                key = "outbox-relay",
                label = "Publisher outbox relayed both messages to Rabbit",
                done = publishOrigin == "direct",
            },
            new { key = "inventory", label = "Consumer processed command (inbox) and published outcome via outbox", done = terminal },
            new
            {
                key = "terminal",
                label = order.Status switch
                {
                    OrderStatus.Fulfilled => "Publisher consumed OrderFulfilled (inbox)",
                    OrderStatus.Failed => "Publisher consumed OrderFailed (inbox)",
                    _ => "Awaiting OrderFulfilled or OrderFailed on publisher inbox",
                },
                done = terminal,
            },
        },
    };
    return TypedResults.Ok(flow);
});

app.MapPost("/api/orders/oversized", async Task<IResult> (HttpContext http) =>
{
    var db = http.RequestServices.GetRequiredService<PublisherDbContext>();
    var scopeFactory = http.RequestServices.GetRequiredService<IServiceScopeFactory>();
    var time = http.RequestServices.GetRequiredService<TimeProvider>();
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
    var orderIdStr = order.Id.ToString("D");
    db.OutboxMessages.Add(
        new OrderPlaced
        {
            OrderId = orderIdStr,
            BulkPaddingForDemo = new string('x', 50_000),
        },
        new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) });
    try
    {
        await db.SaveChangesAsync();
        return TypedResults.Json(new { error = "expected oversized staging to fail SaveChanges" }, statusCode: 500);
    }
    catch (Exception ex)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db2 = scope.ServiceProvider.GetRequiredService<PublisherDbContext>();
        var orderRowExists = await db2.Orders.AsNoTracking().AnyAsync(o => o.Id == order.Id);
        return TypedResults.Ok(new { saveFailed = true, orderRowExists, message = ex.Message });
    }
});

var playground = app.MapGroup("/api/playground")
    .RequireCors("LocalDashboard")
    .RequireAuthorization("DevOnlyNoAuth")
    .AddEndpointFilter(async (EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
    {
        if (!context.HttpContext.RequestServices.GetRequiredService<IOptions<PlaygroundOptions>>().Value.Enabled)
            return Results.NotFound();
        return await next(context);
    });

playground.MapGet("/activities", ([FromServices] PlaygroundActivityRecorder recorder, Guid? orderId, string? scenarioRunId) =>
{
    if (!string.IsNullOrEmpty(scenarioRunId))
        return TypedResults.Ok(recorder.GetEntriesForScenarioRun(scenarioRunId));
    if (orderId is { } id)
        return TypedResults.Ok(recorder.GetEntriesForOrder(id));
    return TypedResults.Ok(recorder.GetRecentEntries());
});

playground.MapGet("/control-state", (
    [FromServices] OrderConsumePlaygroundState orderConsume,
    [FromServices] OutboxFailureState outboxFailure,
    [FromServices] InventoryDemoModeState inventory,
    [FromServices] NotificationPlaygroundState notifications) =>
{
    var (fulfilledMode, fulfilledRemaining) = orderConsume.GetOrderFulfilledApi();
    var (failedMode, failedRemaining) = orderConsume.GetOrderFailedApi();
    var (outboxMode, outboxRemaining) = outboxFailure.GetApi();
    return TypedResults.Ok(new
    {
        service = "playgroundhost",
        toggles = new object[]
        {
            new
            {
                key = "simulate-outbox-transport-failure",
                label = "Simulate Rabbit + EF Core transport send failures (outbox relay + PublishDirect)",
                kind = "outboxSendOutcome",
                mode = outboxMode,
                failuresRemaining = outboxRemaining,
                hint = "succeed | fail | succeed-after (failureCount). Wraps all IMessageSender instances.",
            },
            new
            {
                key = "consume-orderfulfilled-inbox",
                label = "Consume OrderFulfilled (inbox, publisher DB)",
                kind = "inboxHandlerOutcome",
                mode = fulfilledMode,
                failuresRemaining = fulfilledRemaining,
            },
            new
            {
                key = "consume-orderfailed-inbox",
                label = "Consume OrderFailed (inbox, publisher DB)",
                kind = "inboxHandlerOutcome",
                mode = failedMode,
                failuresRemaining = failedRemaining,
            },
            new
            {
                key = "consume-processordercommand-inbox",
                label = "Consume ProcessOrderCommand (inbox, consumer DB)",
                kind = "inventoryCommandMode",
                mode = inventory.Mode.ToString().ToLowerInvariant(),
                failuresRemaining = inventory.Mode == InventoryDemoMode.SucceedAfter ? inventory.SucceedAfterFailuresRemaining : 0,
                succeedAfterBudget = inventory.Mode == InventoryDemoMode.SucceedAfter ? inventory.SucceedAfterInitialBudget : 0,
            },
            new
            {
                key = "consume-orderplaced-rabbit",
                label = "Consume OrderPlaced — notify (Rabbit, no inbox)",
                kind = "rabbitInlineHandlerOutcome",
                mode = notifications.GetOrderPlacedNotifyApi().Mode,
                failuresRemaining = notifications.GetOrderPlacedNotifyApi().FailuresRemaining,
            },
            new
            {
                key = "consume-orderplaced-analytics-rabbit",
                label = "Consume OrderPlaced — analytics (same queue, fan-out)",
                kind = "rabbitInlineHandlerOutcome",
                mode = notifications.GetOrderPlacedAnalyticsApi().Mode,
                failuresRemaining = notifications.GetOrderPlacedAnalyticsApi().FailuresRemaining,
            },
            new
            {
                key = "consume-orderfulfilled-rabbit",
                label = "Consume OrderFulfilled (Rabbit, no inbox)",
                kind = "rabbitInlineHandlerOutcome",
                mode = notifications.GetOrderFulfilledNotifyApi().Mode,
                failuresRemaining = notifications.GetOrderFulfilledNotifyApi().FailuresRemaining,
            },
        },
    });
});

playground.MapPost("/toggle", (
    [FromServices] OrderConsumePlaygroundState orderConsume,
    [FromServices] OutboxFailureState outboxFailure,
    [FromServices] InventoryDemoModeState inventory,
    [FromServices] NotificationPlaygroundState notifications,
    [FromBody] PlaygroundToggleRequest body) =>
{
    string next;
    int failuresRemaining;
    switch (body.Key)
    {
        case "simulate-outbox-transport-failure":
            if (string.IsNullOrEmpty(body.Mode))
            {
                outboxFailure.Cycle();
                (next, failuresRemaining) = outboxFailure.GetApi();
            }
            else
            {
                outboxFailure.Apply(body.Mode, body.FailureCount);
                (next, failuresRemaining) = outboxFailure.GetApi();
            }

            break;
        case "consume-orderfulfilled-inbox":
            if (string.IsNullOrEmpty(body.Mode))
            {
                orderConsume.CycleOrderFulfilled();
                (next, failuresRemaining) = orderConsume.GetOrderFulfilledApi();
            }
            else
            {
                orderConsume.ApplyOrderFulfilled(body.Mode, body.FailureCount);
                (next, failuresRemaining) = orderConsume.GetOrderFulfilledApi();
            }

            break;
        case "consume-orderfailed-inbox":
            if (string.IsNullOrEmpty(body.Mode))
            {
                orderConsume.CycleOrderFailed();
                (next, failuresRemaining) = orderConsume.GetOrderFailedApi();
            }
            else
            {
                orderConsume.ApplyOrderFailed(body.Mode, body.FailureCount);
                (next, failuresRemaining) = orderConsume.GetOrderFailedApi();
            }

            break;
        case "consume-processordercommand-inbox":
            if (string.IsNullOrEmpty(body.Mode))
                inventory.Cycle();
            else
                inventory.ApplyFromToggle(body.Mode, body.FailureCount);
            next = inventory.Mode.ToString().ToLowerInvariant();
            failuresRemaining = inventory.Mode == InventoryDemoMode.SucceedAfter ? inventory.SucceedAfterFailuresRemaining : 0;
            break;
        case "consume-orderplaced-rabbit":
            if (string.IsNullOrEmpty(body.Mode))
                notifications.CycleOrderPlacedNotify();
            else
                notifications.ApplyOrderPlacedNotify(body.Mode, body.FailureCount);
            (next, failuresRemaining) = notifications.GetOrderPlacedNotifyApi();
            break;
        case "consume-orderplaced-analytics-rabbit":
            if (string.IsNullOrEmpty(body.Mode))
                notifications.CycleOrderPlacedAnalytics();
            else
                notifications.ApplyOrderPlacedAnalytics(body.Mode, body.FailureCount);
            (next, failuresRemaining) = notifications.GetOrderPlacedAnalyticsApi();
            break;
        case "consume-orderfulfilled-rabbit":
            if (string.IsNullOrEmpty(body.Mode))
                notifications.CycleOrderFulfilledNotify();
            else
                notifications.ApplyOrderFulfilledNotify(body.Mode, body.FailureCount);
            (next, failuresRemaining) = notifications.GetOrderFulfilledNotifyApi();
            break;
        default:
            throw new BadHttpRequestException($"Unknown toggle key '{body.Key}'.");
    }

    return TypedResults.Ok(new { key = body.Key, mode = next, failuresRemaining });
});

playground.MapGet("/diagnostics/poisoned-summary", async (
    [FromServices] PublisherDbContext publisherDb,
    [FromServices] ConsumerDbContext consumerDb,
    CancellationToken cancellationToken) =>
{
    var publisherOutboxPoisoned = await PlaygroundSqlMetrics.CountPoisonedOutboxAsync(publisherDb, cancellationToken);
    var publisherInboxPoisoned = await PlaygroundSqlMetrics.CountPoisonedInboxAsync(publisherDb, cancellationToken);
    var consumerInboxPoisoned = await PlaygroundSqlMetrics.CountPoisonedInboxAsync(consumerDb, cancellationToken);
    return TypedResults.Ok(new
    {
        publisher = new { outboxPoisoned = publisherOutboxPoisoned, inboxPoisoned = publisherInboxPoisoned },
        consumer = new { inboxPoisoned = consumerInboxPoisoned },
    });
});

playground.MapGet("/scenarios", ([FromServices] ScenarioRunService runs) => TypedResults.Ok(runs.ListCatalog()));

playground.MapPost("/scenarios/{slug}/run", async Task<IResult> (HttpContext http, [FromServices] ScenarioRunService runs) =>
{
    var slug = http.Request.RouteValues["slug"]?.ToString() ?? "";
    var result = await runs.StartRunAsync(slug, http.RequestAborted);
    if (result.Error is { } err)
        return TypedResults.BadRequest(new { error = err });
    return TypedResults.Accepted($"/api/playground/runs/{result.RunId}", new { runId = result.RunId, title = result.Title });
});

playground.MapGet("/runs/{runId:guid}", async Task<IResult> (HttpContext http, Guid runId, [FromServices] ScenarioRunService runs) =>
{
    var row = await runs.GetStatusAsync(runId, http.RequestAborted);
    if (row is null) return TypedResults.NotFound();
    return TypedResults.Ok(row);
});

playground.MapPost("/runs/{runId:guid}/cancel", async Task<IResult> (HttpContext http, Guid runId, [FromServices] ScenarioRunService runs) =>
{
    var ok = await runs.RequestCancelAsync(runId, http.RequestAborted);
    return ok ? TypedResults.Ok(new { cancelled = true }) : TypedResults.NotFound();
});

app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

static async Task<uint> SafeMessageCountAsync(IChannel channel, string queueName, CancellationToken cancellationToken)
{
    try
    {
        return await channel.MessageCountAsync(queueName, cancellationToken);
    }
    catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
    {
        return 0;
    }
}
