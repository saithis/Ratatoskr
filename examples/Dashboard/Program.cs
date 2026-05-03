// Local development example only. See examples/README.md.
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PlaygroundMessages;
using RabbitMQ.Client;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapStaticAssets();

// Management API base path — must match ManagementApiEndpointExtensions.DefaultBasePath
const string mgmtBase = "ratatoskr/api/v1/efcore/contexts";

async Task<IResult> RabbitDepthsAsync(HttpContext http)
{
    var cancellationToken = http.RequestAborted;
    var config = http.RequestServices.GetRequiredService<IConfiguration>();
    var cs = config.GetConnectionString("rabbitmq");
    if (string.IsNullOrEmpty(cs))
        return TypedResults.Ok(new { configured = false, queues = Array.Empty<object>(), note = "Add RabbitMQ reference to Dashboard in AppHost." });

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
}

app.MapGet("/api/config", (IConfiguration config) =>
{
    var orderServiceUrl = config["OrderService:ManagementUrl"]
        ?? throw new InvalidOperationException("OrderService:ManagementUrl is not configured.");
    var inventoryServiceUrl = config["InventoryService:ManagementUrl"]
        ?? throw new InvalidOperationException("InventoryService:ManagementUrl is not configured.");
    var notificationServiceUrl = config["NotificationService:BaseUrl"]
        ?? throw new InvalidOperationException("NotificationService:BaseUrl is not configured.");
    var rabbitConfigured = !string.IsNullOrEmpty(config.GetConnectionString("rabbitmq"));

    return TypedResults.Ok(new
    {
        orderServiceUrl,
        orderManagementPath = $"{mgmtBase}/OrdersDbContext",
        inventoryServiceUrl,
        inventoryManagementPath = $"{mgmtBase}/InventoryDbContext",
        notificationServiceUrl,
        rabbitConfigured,
    });
});

app.MapGet("/api/playground/rabbit-depths", async (HttpContext http) =>
{
    // RequestDelegate is Task-based; returning Task<IResult> alone does not write the body (ASP0016).
    var result = await RabbitDepthsAsync(http);
    await result.ExecuteAsync(http);
});

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

