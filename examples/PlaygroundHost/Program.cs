// Local development example only. See examples/README.md.
using Microsoft.AspNetCore.Http;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlaygroundHost.Composition;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq;
using PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry;
using PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess;
using PlaygroundHost.Scenarios.Inbox.BusinessRejection;
using PlaygroundHost.Scenarios.Inbox.InboxPoison;
using PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess;
using PlaygroundHost.Scenarios.Other.EfcoreInternalCommand;
using PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced;
using PlaygroundHost.Scenarios.Other.ReplayDedups;
using PlaygroundHost.Scenarios.Outbox.OutboxPoison;
using PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess;
using PlaygroundHost.Scenarios.Outbox.OutboxSuccess;
using PlaygroundHost.Scenarios.Outbox.OversizedPayloadRollsBack;
using PlaygroundHost.Scenarios.Tests.BlockingHold;
using PlaygroundHost.Scenarios.Tests.CancelSmoke;
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
builder.Services.AddSingleton<OutboxSendFailureRegistry>();
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

    PlaygroundRatatoskrRegistrations.AddAllPipelineScenarios(bus);
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
builder.Services.AddSingleton<IScenario, OversizedPayloadRollsBackScenario>();
builder.Services.AddSingleton<IScenario, InboxRetryThenSuccessScenario>();
builder.Services.AddSingleton<IScenario, InboxPoisonScenario>();
builder.Services.AddSingleton<IScenario, BusinessRejectionScenario>();
builder.Services.AddSingleton<IScenario, DirectConsumeSuccessScenario>();
builder.Services.AddSingleton<IScenario, DirectConsumeRetryScenario>();
builder.Services.AddSingleton<IScenario, DirectConsumeDlqScenario>();
builder.Services.AddSingleton<IScenario, FanoutTwoHandlersOnOrderplacedScenario>();
builder.Services.AddSingleton<IScenario, EfcoreInternalCommandScenario>();
builder.Services.AddSingleton<IScenario, ReplayDedupsScenario>();
builder.Services.AddSingleton<IScenario, BlockingHoldScenario>();
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
    foreach (var slug in (string[])
             [
                 "outbox-success",
                 "outbox-retry-then-success",
                 "outbox-poison",
                 "inbox-retry-then-success",
                 "inbox-poison",
                 "business-rejection",
                 "direct-consume-success",
                 "direct-consume-retry",
                 "direct-consume-dlq",
                 "fanout-two-handlers-on-orderplaced",
                 "efcore-internal-command",
                 "replay-dedups",
             ])
    {
        foreach (var (key, main) in new[]
                 {
                     ("orders", PlaygroundAmqpNames.OrdersQueue(slug)),
                     ("inventory", PlaygroundAmqpNames.InventoryQueue(slug)),
                     ("notifications", PlaygroundAmqpNames.NotificationsQueue(slug)),
                 })
        {
            var mainCount = await SafeMessageCountAsync(channel, main, cancellationToken);
            var retry = await SafeMessageCountAsync(channel, PlaygroundAmqpNames.RetryQueueName(main), cancellationToken);
            var dlq = await SafeMessageCountAsync(channel, PlaygroundAmqpNames.DlqQueueName(main), cancellationToken);
            queues.Add(new
            {
                slug,
                key,
                mainQueue = main,
                main = mainCount,
                retry,
                dlq,
                retryDelaySeconds = 5,
            });
        }
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

app.MapGet("/api/orders", async ([FromServices] PublisherDbContext db) =>
{
    var orders = await db.Orders
        .OrderByDescending(o => o.CreatedAt)
        .Take(50)
        .Select(o => new { o.Id, o.Status, o.CreatedAt, o.StatusChangedAt, o.PublishOrigin })
        .ToListAsync();
    return TypedResults.Ok(orders);
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

playground.MapPost("/scenarios/{slug}/run", async Task<IResult> (
    HttpContext http,
    [FromRoute] string slug,
    [FromServices] ScenarioRunService runs,
    [FromQuery] bool confirmDanger = false) =>
{
    var result = await runs.StartRunAsync(slug, confirmDanger, http.RequestAborted);
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
