using System.Diagnostics;
using RabbitMQ.Client.Events;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Centralizes all OpenTelemetry instrumentation (tracing and metrics) for the RabbitMQ transport.
/// Covers both the send (producer) and receive/process (consumer) sides.
/// </summary>
internal class RabbitMqTelemetry(RabbitMqOptions options, TimeProvider timeProvider)
{
    // ─── Send-side (producer) ──────────────────────────────────────

    /// <summary>
    /// Starts a "send {destination}" activity and sets trace context on the message properties.
    /// </summary>
    public Activity? StartSendActivity(
        MessageProperties props,
        int contentLength,
        string destination,
        string routingKey
    )
    {
        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            $"send {destination}",
            ActivityKind.Client,
            Activity.Current?.Context ?? default
        );

        if (activity != null)
        {
            props.TraceParent = activity.Id;
            props.TraceState = activity.TraceStateString;

            activity.SetTag(MessagingSemanticConventions.OperationName, "send");
            activity.SetTag(
                MessagingSemanticConventions.OperationType,
                MessagingSemanticConventions.OperationTypeSend
            );
            activity.SetTag(MessagingSemanticConventions.System, "rabbitmq");
            activity.SetTag(MessagingSemanticConventions.DestinationName, destination);
            activity.SetTag(MessagingSemanticConventions.RabbitMqRoutingKey, routingKey);
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, contentLength);
            activity.SetTag(
                MessagingSemanticConventions.ServerAddress,
                options.ConnectionString?.Host
            );
            activity.SetTag(
                MessagingSemanticConventions.ServerPort,
                options.ConnectionString?.Port
            );
        }

        return activity;
    }

    /// <summary>
    /// Records send metrics: operation duration and sent message count.
    /// </summary>
    public void RecordSent(
        long startTimestamp,
        Exception? sendException,
        string destination,
        string routingKey
    )
    {
        var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;

        var tags = new TagList
        {
            { MessagingSemanticConventions.System, "rabbitmq" },
            { MessagingSemanticConventions.OperationName, "send" },
            {
                MessagingSemanticConventions.OperationType,
                MessagingSemanticConventions.OperationTypeSend
            },
            { MessagingSemanticConventions.DestinationName, destination },
            { MessagingSemanticConventions.RabbitMqRoutingKey, routingKey },
            { MessagingSemanticConventions.ServerAddress, options.ConnectionString?.Host },
            { MessagingSemanticConventions.ServerPort, options.ConnectionString?.Port },
        };

        if (sendException != null)
        {
            tags.Add(MessagingSemanticConventions.ErrorType, sendException.GetType().FullName);
        }

        RatatoskrDiagnostics.ClientOperationDuration.Record(duration, tags);
        RatatoskrDiagnostics.ClientSentMessages.Add(1, tags);
    }

    /// <summary>
    /// Sets error information on an activity when a send or process fails.
    /// </summary>
    public static void SetActivityError(Activity? activity, Exception ex)
    {
        activity?.SetTag(MessagingSemanticConventions.ErrorType, ex.GetType().FullName);
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    }

    // ─── Receive-side (consumer) ───────────────────────────────────

    public TagList CreateConsumeTags(
        BasicDeliverEventArgs ea,
        MessageProperties props,
        string queueName
    )
    {
        var (originalExchange, originalRoutingKey) =
            RabbitMqHeaderHelper.GetOriginalDestinationFromHeaders(ea.BasicProperties.Headers);
        var destinationName = props.GetExchange() ?? originalExchange ?? ea.Exchange;
        var routingKey = props.GetRoutingKey() ?? originalRoutingKey ?? ea.RoutingKey;

        return BuildTagList(destinationName, routingKey, queueName);
    }

    public TagList CreateConsumeFallbackTags(BasicDeliverEventArgs ea, string queueName)
    {
        var (originalExchange, originalRoutingKey) =
            RabbitMqHeaderHelper.GetOriginalDestinationFromHeaders(ea.BasicProperties.Headers);
        var destinationName = originalExchange ?? ea.Exchange;
        var routingKey = originalRoutingKey ?? ea.RoutingKey;

        return BuildTagList(destinationName, routingKey, queueName);
    }

    private TagList BuildTagList(string destinationName, string routingKey, string queueName)
    {
        return new TagList
        {
            { MessagingSemanticConventions.System, "rabbitmq" },
            { MessagingSemanticConventions.OperationName, "process" },
            {
                MessagingSemanticConventions.OperationType,
                MessagingSemanticConventions.OperationTypeProcess
            },
            { MessagingSemanticConventions.DestinationSubscriptionName, queueName },
            { MessagingSemanticConventions.DestinationName, destinationName },
            { MessagingSemanticConventions.RabbitMqRoutingKey, routingKey },
            { MessagingSemanticConventions.ServerAddress, options.ConnectionString?.Host },
            { MessagingSemanticConventions.ServerPort, options.ConnectionString?.Port },
        };
    }

    public Activity? StartConsumeActivity(
        MessageProperties props,
        TagList tags,
        int bodySize,
        ulong deliveryTag
    )
    {
        ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);

        var destinationName =
            tags.FirstOrDefault(t => t.Key == MessagingSemanticConventions.DestinationName).Value
            as string;
        var destination = string.IsNullOrEmpty(destinationName)
            ? tags.FirstOrDefault(t =>
                t.Key == MessagingSemanticConventions.DestinationSubscriptionName
            ).Value as string
            : destinationName;

        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            $"process {destination}",
            ActivityKind.Consumer,
            parentContext
        );

        if (activity != null)
        {
            // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/#messaging-attributes
            // https://opentelemetry.io/docs/specs/semconv/messaging/rabbitmq/
            foreach (var tag in tags)
            {
                activity.SetTag(tag.Key, tag.Value);
            }
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, bodySize);
            activity.SetTag(MessagingSemanticConventions.RabbitMqDeliveryTag, (long)deliveryTag);
        }
        return activity;
    }

    public void RecordReceived(
        TagList tags,
        DateTimeOffset? messageTime,
        DateTimeOffset receivedTimestamp
    )
    {
        RatatoskrDiagnostics.ClientConsumedMessages.Add(1, tags);

        if (messageTime.HasValue)
        {
            var lag = Math.Max((receivedTimestamp - messageTime.Value).TotalSeconds, 0);
            RatatoskrDiagnostics.ReceiveLag.Record(lag, tags);
        }
    }

    public void RecordProcessed(
        TagList tags,
        long processStartTimestamp,
        DateTimeOffset? messageTime,
        string? errorType
    )
    {
        if (tags.Count > 0)
        {
            if (errorType != null)
            {
                tags.Add(MessagingSemanticConventions.ErrorType, errorType);
            }

            RatatoskrDiagnostics.ProcessDuration.Record(
                Stopwatch.GetElapsedTime(processStartTimestamp).TotalSeconds,
                tags
            );

            if (messageTime.HasValue)
            {
                var lag = Math.Max((timeProvider.GetUtcNow() - messageTime.Value).TotalSeconds, 0);
                RatatoskrDiagnostics.ProcessLag.Record(lag, tags);
            }
        }
    }

    public void RecordRetry(BasicDeliverEventArgs ea, string queueName)
    {
        var tags = CreateConsumeFallbackTags(ea, queueName);
        RatatoskrDiagnostics.RetryMessages.Add(1, tags);
    }

    public void RecordDeadLetter(BasicDeliverEventArgs ea, string queueName)
    {
        var tags = CreateConsumeFallbackTags(ea, queueName);
        RatatoskrDiagnostics.DeadLetterMessages.Add(1, tags);
    }
}
