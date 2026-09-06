using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Management.Agent;

/// <summary>
/// Background service that periodically emits heartbeat announcements over RabbitMQ to the UI.
/// </summary>
public sealed class ServiceHeartbeatReporter(
    ManagementRequestHandler requestHandler,
    IOptions<RatatoskrManagementOptions> options,
    ILogger<ServiceHeartbeatReporter> logger,
    RabbitMqConnectionManager? connectionManager = null
) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "SendChannel is reusable and managed by RabbitMqConnectionManager")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        if (!opt.EnableHeartbeat || connectionManager == null)
        {
            return;
        }

        var inboxExchange = $"{opt.UiExchangePrefix}.inbox";

        // Initial delay to let connections and topology stabilize
        await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var heartbeat = await requestHandler.BuildHeartbeatAsync(stoppingToken);
                var payload = JsonSerializer.SerializeToUtf8Bytes(heartbeat, JsonOptions);

                var channel = await connectionManager.GetOrCreateSendChannelAsync(
                    enablePublisherConfirms: false,
                    stoppingToken
                );

                var props = new BasicProperties
                {
                    Type = "ratatoskr.management.heartbeat",
                    Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                };

                await channel.BasicPublishAsync(
                    exchange: inboxExchange,
                    routingKey: "heartbeat",
                    mandatory: false,
                    basicProperties: props,
                    body: payload,
                    cancellationToken: stoppingToken
                );

                logger.LogDebug(
                    "Published heartbeat for service '{Service}' (Instance: {Instance}) to '{Exchange}'",
                    opt.ServiceName,
                    opt.InstanceId,
                    inboxExchange
                );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish heartbeat for service '{Service}'. Retrying in {Interval}s", opt.ServiceName, opt.HeartbeatInterval.TotalSeconds);
            }

            await Task.Delay(opt.HeartbeatInterval, stoppingToken);
        }
    }
}
