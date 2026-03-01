using System.Diagnostics;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Shared OTel instrumentation logic for RabbitMQ senders.
/// Mirrors <see cref="Ratatoskr.Local.LocalSendInstrumentation"/> for the RabbitMQ transport.
/// </summary>
internal static class RabbitMqSendInstrumentation
{
    /// <summary>
    /// Starts a "send {destination}" activity and sets trace context on the message properties.
    /// </summary>
    public static Activity? StartSendActivity(
        MessageProperties props,
        int contentLength,
        string destination,
        string routingKey,
        RabbitMqOptions options)
    {
        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            $"send {destination}",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        if (activity != null)
        {
            props.TraceParent = activity.Id;
            props.TraceState = activity.TraceStateString;

            activity.SetTag(MessagingSemanticConventions.OperationName, "send");
            activity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend);
            activity.SetTag(MessagingSemanticConventions.System, "rabbitmq");
            activity.SetTag(MessagingSemanticConventions.DestinationName, destination);
            activity.SetTag(MessagingSemanticConventions.RabbitMqRoutingKey, routingKey);
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, contentLength);
            activity.SetTag(MessagingSemanticConventions.ServerAddress, options.ConnectionString?.Host);
            activity.SetTag(MessagingSemanticConventions.ServerPort, options.ConnectionString?.Port);
        }

        return activity;
    }

    /// <summary>
    /// Records send metrics (duration + message count) and notifies observers with the Sent stage.
    /// </summary>
    public static async Task RecordSendMetricsAndNotifyAsync(
        long startTimestamp,
        Exception? sendException,
        MessageProperties props,
        byte[] wireBody,
        string transportName,
        TransportMessageSnapshot transportMessage,
        string destination,
        string routingKey,
        RabbitMqOptions options,
        IEnumerable<IMessageActivityObserver> observers,
        TimeProvider timeProvider)
    {
        var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;

        var tags = new TagList
        {
            { MessagingSemanticConventions.System, "rabbitmq" },
            { MessagingSemanticConventions.OperationName, "send" },
            { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend },
            { MessagingSemanticConventions.DestinationName, destination },
            { MessagingSemanticConventions.RabbitMqRoutingKey, routingKey },
            { MessagingSemanticConventions.ServerAddress, options.ConnectionString?.Host },
            { MessagingSemanticConventions.ServerPort, options.ConnectionString?.Port },
        };

        if (sendException != null)
            tags.Add(MessagingSemanticConventions.ErrorType, sendException.GetType().FullName);

        RatatoskrDiagnostics.ClientOperationDuration.Record(duration, tags);
        RatatoskrDiagnostics.ClientSentMessages.Add(1, tags);

        await observers.NotifyAsync(new MessageActivity
        {
            Stage = MessageStage.Sent,
            Properties = props,
            SerializedBody = wireBody,
            TransportName = transportName,
            TransportMessage = transportMessage,
            Exception = sendException,
            Timestamp = timeProvider.GetUtcNow(),
        });
    }

    /// <summary>
    /// Sets error information on an activity when a send fails.
    /// </summary>
    public static void SetActivityError(Activity? activity, Exception ex)
    {
        activity?.SetTag(MessagingSemanticConventions.ErrorType, ex.GetType().FullName);
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    }
}
