using System.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Centralizes all OpenTelemetry instrumentation (tracing and metrics) for the EF Core transport.
/// Covers the send (producer) side — messages written directly to inbox tables.
/// </summary>
internal class EfCoreTelemetry
{
    /// <summary>
    /// Starts a "send efcore" activity and sets trace context on the message properties.
    /// </summary>
    public Activity? StartSendActivity(MessageProperties props, int contentLength)
    {
        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "send efcore",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        if (activity != null)
        {
            props.TraceParent = activity.Id;
            props.TraceState = activity.TraceStateString;

            activity.SetTag(MessagingSemanticConventions.OperationName, "send");
            activity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend);
            activity.SetTag(MessagingSemanticConventions.System, "efcore");
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, contentLength);
        }

        return activity;
    }

    /// <summary>
    /// Records send metrics: operation duration and sent message count.
    /// </summary>
    public void RecordSent(long startTimestamp, Exception? sendException)
    {
        var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;

        var tags = new TagList
        {
            { MessagingSemanticConventions.System, "efcore" },
            { MessagingSemanticConventions.OperationName, "send" },
            { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend },
        };

        if (sendException != null)
            tags.Add(MessagingSemanticConventions.ErrorType, sendException.GetType().FullName);

        RatatoskrDiagnostics.ClientOperationDuration.Record(duration, tags);
        RatatoskrDiagnostics.ClientSentMessages.Add(1, tags);
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
