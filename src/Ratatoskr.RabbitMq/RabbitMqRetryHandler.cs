using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Handles retry logic for failed messages.
/// </summary>
internal class RabbitMqRetryHandler(ILogger<RabbitMqRetryHandler> logger)
{

    /// <summary>
    /// Determines how to handle a failed message based on retry configuration.
    /// </summary>
    public async Task HandleFailureAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        RabbitMqChannelOptions config,
        string queueName,
        DispatchResult result,
        CancellationToken cancellationToken)
    {
        var messageId = ea.BasicProperties.MessageId ?? "unknown";

        // Permanent errors go straight to DLQ
        if (result == DispatchResult.PermanentError || result == DispatchResult.NoHandlers)
        {
            logger.LogWarning("Permanent error for message '{MessageId}', sending to DLQ", messageId);
            await RejectToDlqAsync(channel, ea, config, queueName, cancellationToken);
            return;
        }

        // Check retry count
        var retryCount = GetRetryCount(ea.BasicProperties.Headers);

        if (retryCount >= config.Retry.MaxRetries)
        {
            logger.LogError(
                "Message '{MessageId}' exceeded max retries ({MaxRetries}), sending to DLQ",
                messageId, config.Retry.MaxRetries);
            await RejectToDlqAsync(channel, ea, config, queueName, cancellationToken);
        }
        else
        {
            logger.LogInformation(
                "Message '{MessageId}' will be retried (attempt {RetryCount}/{MaxRetries})",
                messageId, retryCount + 1, config.Retry.MaxRetries);

            var (destinationName, routingKey) = RabbitMqHeaderHelper.GetOriginalDestinationFromHeaders(ea.BasicProperties.Headers);
            destinationName ??= ea.Exchange;
            routingKey ??= ea.RoutingKey;

            var tags = new TagList
            {
                { "messaging.system", "rabbitmq" },
                { "messaging.destination.subscription.name", queueName },
                { "messaging.destination.name", destinationName },
                { "messaging.rabbitmq.destination.routing_key", routingKey }
            };
            RatatoskrDiagnostics.RetryMessages.Add(1, tags);

            // Reject without requeue - DLX will route to retry queue
            await channel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);
        }
    }

    private async Task RejectToDlqAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        RabbitMqChannelOptions config,
        string queueName,
        CancellationToken cancellationToken)
    {
        var (destinationName, routingKey) = RabbitMqHeaderHelper.GetOriginalDestinationFromHeaders(ea.BasicProperties.Headers);
        destinationName ??= ea.Exchange;
        routingKey ??= ea.RoutingKey;

        var tags = new TagList
        {
            { "messaging.system", "rabbitmq" },
            { "messaging.destination.subscription.name", queueName },
            { "messaging.destination.name", destinationName },
            { "messaging.rabbitmq.destination.routing_key", routingKey }
        };
        RatatoskrDiagnostics.DeadLetterMessages.Add(1, tags);

        // Need to manually publish to DLQ since we can't conditionally route via DLX
        if (config.Retry.UseManaged)
        {
            var dlqName = $"{queueName}{config.Retry.DeadLetterSuffix}";

            // Copy properties and add metadata about failure
            var props = new BasicProperties
            {
                MessageId = ea.BasicProperties.MessageId,
                ContentType = ea.BasicProperties.ContentType,
                DeliveryMode = ea.BasicProperties.DeliveryMode,
                Type = ea.BasicProperties.Type,
                Headers = new Dictionary<string, object?>(ea.BasicProperties.Headers ?? new Dictionary<string, object?>())
            };

            props.Headers["x-original-queue"] = queueName;
            props.Headers["x-failure-time"] = DateTimeOffset.UtcNow.ToString("O");

            // Publish to DLQ Exchange
            await channel.BasicPublishAsync(
                exchange: dlqName, // Publish to the DLQ Exchange (Fanout)
                routingKey: "",    // Routing key is ignored for Fanout
                mandatory: false,
                basicProperties: props,
                body: ea.Body,
                cancellationToken: cancellationToken);

            // ACK original message
            await channel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);
        }
        else
        {
            // Just reject without requeue - let DLX handle it
            await channel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);
        }
    }

    private static int GetRetryCount(IDictionary<string, object?>? headers)
    {
        if (headers == null) return 0;

        // Use x-death header to track retry attempts (automatically managed by RabbitMQ DLX)
        if (headers.TryGetValue("x-death", out var xDeathObj) && xDeathObj is System.Collections.IEnumerable xDeathList)
        {
            long totalCount = 0;
            foreach (var entryObj in xDeathList)
            {
                if (entryObj is IDictionary<string, object> entry)
                {
                    if (entry.TryGetValue("count", out var countObj) &&
                        entry.TryGetValue("reason", out var reasonObj))
                    {
                        var reason = RabbitMqHeaderHelper.ConvertHeaderToString(reasonObj);
                        if (reason == "rejected")
                        {
                            totalCount += Convert.ToInt64(countObj);
                        }
                    }
                }
            }
            return (int)totalCount;
        }

        return 0;
    }
}
