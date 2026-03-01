using System.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr.Local;

/// <summary>
/// Shared OTel instrumentation logic for local transport senders.
/// Used by both <see cref="LocalMessageSender"/> and the durable variant in Ratatoskr.EfCore.
/// </summary>
internal static class LocalSendInstrumentation
{
    /// <summary>
    /// Starts a "send local" activity and sets trace context on the message properties.
    /// </summary>
    public static Activity? StartSendActivity(MessageProperties props, int contentLength)
    {
        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "send local",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        if (activity != null)
        {
            props.TraceParent = activity.Id;
            props.TraceState = activity.TraceStateString;

            activity.SetTag(MessagingSemanticConventions.OperationName, "send");
            activity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend);
            activity.SetTag(MessagingSemanticConventions.System, "local");
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, contentLength);
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
        byte[] content,
        string transportName,
        TransportMessageSnapshot transportMessage,
        IEnumerable<IMessageActivityObserver> observers,
        TimeProvider timeProvider)
    {
        var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        var tags = new TagList
        {
            { MessagingSemanticConventions.System, "local" },
            { MessagingSemanticConventions.OperationName, "send" },
            { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend },
        };

        if (sendException != null)
            tags.Add(MessagingSemanticConventions.ErrorType, sendException.GetType().FullName);

        RatatoskrDiagnostics.ClientOperationDuration.Record(duration, tags);
        RatatoskrDiagnostics.ClientSentMessages.Add(1, tags);

        var sentTimestamp = timeProvider.GetUtcNow();
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnMessageActivity(new MessageActivity
                {
                    Stage = MessageStage.Sent,
                    Properties = props,
                    SerializedBody = content,
                    TransportName = transportName,
                    TransportMessage = transportMessage,
                    Exception = sendException,
                    Timestamp = sentTimestamp,
                });
            }
            catch
            {
                // Observer failures must not affect the pipeline
            }
        }
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
