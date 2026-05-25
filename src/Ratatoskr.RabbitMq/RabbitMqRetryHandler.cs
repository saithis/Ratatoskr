using System.Globalization;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Handles retry logic for failed messages.
/// </summary>
internal partial class RabbitMqRetryHandler(
    RabbitMqTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<RabbitMqRetryHandler> logger
)
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
        CancellationToken cancellationToken
    )
    {
        var messageId = ea.BasicProperties.MessageId ?? "unknown";

        // Permanent errors go straight to DLQ
        if (result is DispatchResult.PermanentError or DispatchResult.NoHandlers)
        {
            LogPermanentErrorSendingToDlq(logger, messageId);
            await RejectToDlqAsync(channel, ea, config, queueName, cancellationToken);
            return;
        }

        // Check retry count
        var retryCount = GetRetryCount(ea.BasicProperties.Headers);

        if (retryCount >= config.Retry.MaxRetries)
        {
            LogMessageExceededMaxRetries(logger, messageId, config.Retry.MaxRetries);
            await RejectToDlqAsync(channel, ea, config, queueName, cancellationToken);
        }
        else
        {
            LogMessageWillBeRetried(logger, messageId, retryCount + 1, config.Retry.MaxRetries);

            // Reject without requeue - DLX will route to retry queue
            await channel.BasicNackAsync(
                ea.DeliveryTag,
                multiple: false,
                requeue: false,
                cancellationToken
            );

            telemetry.RecordRetry(ea, queueName);
        }
    }

    private async Task RejectToDlqAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        RabbitMqChannelOptions config,
        string queueName,
        CancellationToken cancellationToken
    )
    {
        // Need to manually publish to DLQ since we can't conditionally route via DLX
        if (config.Retry.UseManaged)
        {
            var dlqName = queueName + config.Retry.DeadLetterSuffix;

            // Copy properties and add metadata about failure
            var props = new BasicProperties
            {
                MessageId = ea.BasicProperties.MessageId,
                ContentType = ea.BasicProperties.ContentType,
                DeliveryMode = ea.BasicProperties.DeliveryMode,
                Type = ea.BasicProperties.Type,
                Headers = new Dictionary<string, object?>(
                    ea.BasicProperties.Headers
                        ?? new Dictionary<string, object?>(StringComparer.Ordinal),
                    StringComparer.Ordinal
                ),
            };

            props.Headers["x-original-queue"] = queueName;
            props.Headers["x-failure-time"] = timeProvider.GetUtcNow().ToString("O");

            // Publish to DLQ Exchange
            await channel.BasicPublishAsync(
                exchange: dlqName, // Publish to the DLQ Exchange (Fanout)
                routingKey: "", // Routing key is ignored for Fanout
                mandatory: false,
                basicProperties: props,
                body: ea.Body,
                cancellationToken: cancellationToken
            );

            // ACK original message
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
        }
        else
        {
            // Just reject without requeue - let DLX handle it
            await channel.BasicNackAsync(
                ea.DeliveryTag,
                multiple: false,
                requeue: false,
                cancellationToken
            );
        }

        telemetry.RecordDeadLetter(ea, queueName);
    }

    private static int GetRetryCount(IDictionary<string, object?>? headers)
    {
        if (headers == null)
        {
            return 0;
        }

        // Use x-death header to track retry attempts (automatically managed by RabbitMQ DLX)
        if (
            headers.TryGetValue("x-death", out var xDeathObj)
            && xDeathObj is System.Collections.IEnumerable xDeathList
        )
        {
            long totalCount = 0;
            foreach (var entryObj in xDeathList)
            {
                if (
                    entryObj is IDictionary<string, object> entry
                    && entry.TryGetValue("count", out var countObj)
                    && entry.TryGetValue("reason", out var reasonObj)
                )
                {
                    var reason = RabbitMqHeaderHelper.ConvertHeaderToString(reasonObj);
                    if (reason == "rejected")
                    {
                        totalCount += Convert.ToInt64(countObj, CultureInfo.InvariantCulture);
                    }
                }
            }
            return (int)totalCount;
        }

        return 0;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Permanent error for message '{MessageId}', sending to DLQ"
    )]
    private static partial void LogPermanentErrorSendingToDlq(ILogger logger, string messageId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Message '{MessageId}' exceeded max retries ({MaxRetries}), sending to DLQ"
    )]
    private static partial void LogMessageExceededMaxRetries(
        ILogger logger,
        string messageId,
        int maxRetries
    );

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Message '{MessageId}' will be retried (attempt {RetryCount}/{MaxRetries})"
    )]
    private static partial void LogMessageWillBeRetried(
        ILogger logger,
        string messageId,
        int retryCount,
        int maxRetries
    );
}
