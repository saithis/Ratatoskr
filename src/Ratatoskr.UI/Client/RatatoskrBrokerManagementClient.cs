using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.UI.Client;

/// <summary>
/// Implements management communication over RabbitMQ 2-exchange topology or in-process dispatch.
/// </summary>
public sealed class RatatoskrBrokerManagementClient(
    ActiveServiceRegistry registry,
    IOptions<RatatoskrUiOptions> options,
    IServiceProvider serviceProvider,
    ILogger<RatatoskrBrokerManagementClient> logger
) : BackgroundService, IRatatoskrBrokerManagementClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ManagementResponseEnvelope>> _pendingRequests = new(StringComparer.Ordinal);

    public ActiveServiceRegistry Registry => registry;

    private RabbitMqConnectionManager? ConnectionManager => serviceProvider.GetService<RabbitMqConnectionManager>();
    private ManagementRequestHandler? LocalRequestHandler => serviceProvider.GetService<ManagementRequestHandler>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var conn = ConnectionManager;
        if (conn == null)
        {
            logger.LogInformation("RabbitMQ is not registered; Ratatoskr UI is operating in In-Process mode");
            // Auto-populate registry from local service if available
            await RefreshLocalHeartbeatAsync(stoppingToken);
            return;
        }

        var opt = options.Value;
        var commandsExchange = $"{opt.UiExchangePrefix}.commands";
        var inboxExchange = $"{opt.UiExchangePrefix}.inbox";
        var queueName = $"{opt.UiExchangePrefix}.inbox.queue";

        logger.LogInformation(
            "Starting Ratatoskr UI Broker Client (Commands: '{Commands}', Inbox: '{Inbox}', Queue: '{Queue}')",
            commandsExchange,
            inboxExchange,
            queueName
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var channel = await conn.CreateChannelAsync(enablePublisherConfirms: false, stoppingToken);

                // 1. Declare commands Topic Exchange
                await channel.ExchangeDeclareAsync(
                    exchange: commandsExchange,
                    type: ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken
                );

                // 2. Declare inbox Topic Exchange
                await channel.ExchangeDeclareAsync(
                    exchange: inboxExchange,
                    type: ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken
                );

                // 3. Declare UI queue and bind to inbox exchange
                await channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken
                );

                await channel.QueueBindAsync(
                    queue: queueName,
                    exchange: inboxExchange,
                    routingKey: "#",
                    cancellationToken: stoppingToken
                );

                // 4. Consume heartbeats and responses
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    try
                    {
                        var bodyBytes = ea.Body.ToArray();
                        var routingKey = ea.RoutingKey;

                        if (routingKey.Equals("heartbeat", StringComparison.OrdinalIgnoreCase)
                            || ea.BasicProperties.Type == "ratatoskr.management.heartbeat")
                        {
                            var heartbeat = JsonSerializer.Deserialize<ServiceHeartbeat>(bodyBytes, JsonOptions);
                            if (heartbeat != null)
                            {
                                registry.RegisterHeartbeat(heartbeat);
                            }
                        }
                        else
                        {
                            // It's a response to an RPC request
                            var response = JsonSerializer.Deserialize<ManagementResponseEnvelope>(bodyBytes, JsonOptions);
                            var correlationId = ea.BasicProperties.CorrelationId ?? response?.RequestId;

                            if (!string.IsNullOrEmpty(correlationId) && _pendingRequests.TryRemove(correlationId, out var tcs))
                            {
                                if (response != null)
                                {
                                    tcs.TrySetResult(response);
                                }
                                else
                                {
                                    tcs.TrySetException(new InvalidOperationException("Malformed response received"));
                                }
                            }
                        }

                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing incoming UI broker message");
                    }
                };

                await channel.BasicConsumeAsync(
                    queue: queueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken
                );

                logger.LogInformation("Ratatoskr UI Broker Client actively consuming from '{Queue}'", queueName);

                // Broadcast ping to discover any already-running services
                try
                {
                    await channel.BasicPublishAsync(
                        exchange: commandsExchange,
                        routingKey: "*.broadcast",
                        mandatory: false,
                        basicProperties: new BasicProperties { Type = "ratatoskr.management.ping" },
                        body: JsonSerializer.SerializeToUtf8Bytes(new ManagementRequestEnvelope
                        {
                            RequestId = Guid.NewGuid().ToString("N"),
                            Action = "GetStats",
                            TargetService = "*"
                        }, JsonOptions),
                        cancellationToken: stoppingToken
                    );
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not broadcast discovery ping on startup");
                }

                var tcsShutdown = new TaskCompletionSource();
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    tcsShutdown.TrySetResult();
                    return Task.CompletedTask;
                };

                await using var reg = stoppingToken.Register(() => tcsShutdown.TrySetResult());
                await tcsShutdown.Task;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "UI broker consumer disconnected. Reconnecting in 5 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "SendChannel is reusable and managed by RabbitMqConnectionManager")]
    public async Task<TResponse?> ExecuteAsync<TRequest, TResponse>(
        string serviceName,
        string? contextName,
        string action,
        TRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var localHandler = LocalRequestHandler;
        var localOpt = serviceProvider.GetService<IOptions<RatatoskrManagementOptions>>()?.Value;

        // If in-process mode or target service is local
        if (ConnectionManager == null || (localHandler != null && localOpt != null && string.Equals(localOpt.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)))
        {
            if (localHandler == null)
            {
                throw new InvalidOperationException($"Cannot execute management request: no transport or local handler available for '{serviceName}'");
            }

            var envelope = new ManagementRequestEnvelope
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Action = action,
                TargetService = serviceName,
                TargetContext = contextName,
                PayloadJson = JsonSerializer.Serialize(request, JsonOptions)
            };

            var localResponse = await localHandler.HandleAsync(envelope, cancellationToken);
            if (!localResponse.Success)
            {
                throw new InvalidOperationException(localResponse.ErrorMessage ?? "Management operation failed");
            }

            if (string.IsNullOrEmpty(localResponse.PayloadJson))
            {
                return default;
            }

            return JsonSerializer.Deserialize<TResponse>(localResponse.PayloadJson, JsonOptions);
        }

        // RabbitMQ broker execution
        var conn = ConnectionManager!;
        var requestId = Guid.NewGuid().ToString("N");
        var opt = options.Value;
        var commandsExchange = $"{opt.UiExchangePrefix}.commands";

        var requestEnvelope = new ManagementRequestEnvelope
        {
            RequestId = requestId,
            Action = action,
            TargetService = serviceName,
            TargetContext = contextName,
            PayloadJson = JsonSerializer.Serialize(request, JsonOptions)
        };

        var tcs = new TaskCompletionSource<ManagementResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        try
        {
            var channel = await conn.GetOrCreateSendChannelAsync(enablePublisherConfirms: false, cancellationToken);
            var payload = JsonSerializer.SerializeToUtf8Bytes(requestEnvelope, JsonOptions);

            var basicProps = new BasicProperties
            {
                CorrelationId = requestId,
                ReplyTo = $"reply.{requestId}",
                ContentType = "application/json"
            };

            var routingKey = $"{serviceName}.{action}";
            await channel.BasicPublishAsync(
                exchange: commandsExchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: basicProps,
                body: payload,
                cancellationToken: cancellationToken
            );

            using var timeoutCts = new CancellationTokenSource(opt.RequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var responseEnvelope = await tcs.Task.WaitAsync(linkedCts.Token);

            if (!responseEnvelope.Success)
            {
                throw new InvalidOperationException(responseEnvelope.ErrorMessage ?? "Management operation failed");
            }

            if (string.IsNullOrEmpty(responseEnvelope.PayloadJson))
            {
                return default;
            }

            return JsonSerializer.Deserialize<TResponse>(responseEnvelope.PayloadJson, JsonOptions);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Management request '{action}' to service '{serviceName}' timed out after {opt.RequestTimeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}s");
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task RefreshLocalHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (LocalRequestHandler != null)
        {
            try
            {
                var hb = await LocalRequestHandler.BuildHeartbeatAsync(cancellationToken);
                registry.RegisterHeartbeat(hb);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not build local heartbeat for In-Process mode");
            }
        }
    }
}
