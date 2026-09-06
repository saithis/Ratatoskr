using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Management.Agent;

/// <summary>
/// Background service that consumes management requests from RabbitMQ and sends replies.
/// Declares '{ServiceName}.mgmt' and binds to '{UiExchangePrefix}.commands'.
/// </summary>
public sealed class RabbitMqManagementAgentConsumer(
    ManagementRequestHandler requestHandler,
    IOptions<RatatoskrManagementOptions> options,
    ILogger<RabbitMqManagementAgentConsumer> logger,
    RabbitMqConnectionManager? connectionManager = null
) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (connectionManager == null)
        {
            logger.LogDebug("RabbitMQ is not configured; RabbitMqManagementAgentConsumer is disabled.");
            return;
        }

        var opt = options.Value;
        var queueName = $"{opt.ServiceName}.mgmt";
        var commandsExchange = $"{opt.UiExchangePrefix}.commands";
        var inboxExchange = $"{opt.UiExchangePrefix}.inbox";

        logger.LogInformation(
            "Starting Ratatoskr Management Agent for service '{Service}' (Queue: '{Queue}', Commands: '{Commands}', Inbox: '{Inbox}')",
            opt.ServiceName,
            queueName,
            commandsExchange,
            inboxExchange
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var channel = await connectionManager.CreateChannelAsync(enablePublisherConfirms: false, stoppingToken);

                // 1. Declare our service queue ({user}.mgmt) - compliant with configure: {user}\..*
                await channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken
                );

                // 2. Bind to {UiExchangePrefix}.commands
                await channel.QueueBindAsync(
                    queue: queueName,
                    exchange: commandsExchange,
                    routingKey: $"{opt.ServiceName}.#",
                    cancellationToken: stoppingToken
                );

                await channel.QueueBindAsync(
                    queue: queueName,
                    exchange: commandsExchange,
                    routingKey: "*.broadcast",
                    cancellationToken: stoppingToken
                );

                logger.LogInformation(
                    "Successfully bound management queue '{Queue}' to exchange '{Exchange}'",
                    queueName,
                    commandsExchange
                );

                // 3. Start consuming
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    try
                    {
                        var requestJson = System.Text.Encoding.UTF8.GetString(ea.Body.ToArray());
                        var request = JsonSerializer.Deserialize<ManagementRequestEnvelope>(requestJson, JsonOptions);

                        if (request != null)
                        {
                            var response = await requestHandler.HandleAsync(request, stoppingToken);
                            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);

                            var replyProps = new BasicProperties
                            {
                                CorrelationId = ea.BasicProperties.CorrelationId ?? request.RequestId,
                            };

                            var replyRoutingKey = !string.IsNullOrEmpty(ea.BasicProperties.ReplyTo)
                                ? ea.BasicProperties.ReplyTo
                                : $"reply.{request.RequestId}";

                            await channel.BasicPublishAsync(
                                exchange: inboxExchange,
                                routingKey: replyRoutingKey,
                                mandatory: false,
                                basicProperties: replyProps,
                                body: responseBytes,
                                cancellationToken: stoppingToken
                            );
                        }

                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogError(ex, "Error processing management request on queue '{Queue}'", queueName);
                        try
                        {
                            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                        }
                        catch
                        {
                            // ignore channel closure on nack
                        }
                    }
                };

                await channel.BasicConsumeAsync(
                    queue: queueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken
                );

                logger.LogInformation("Ratatoskr Management Agent is actively listening on '{Queue}'", queueName);

                // Wait until cancellation or channel close
                var tcs = new TaskCompletionSource();
                channel.ChannelShutdownAsync += (_, args) =>
                {
                    tcs.TrySetResult();
                    return Task.CompletedTask;
                };

                using var reg = stoppingToken.Register(() => tcs.TrySetResult());
                await tcs.Task;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
            {
                logger.LogInformation(
                    "Commands exchange '{Exchange}' not yet declared; retrying in 2 seconds...",
                    commandsExchange
                );
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Management agent consumer disconnected. Reconnecting in 5 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
