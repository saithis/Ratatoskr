using System.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr.Local;

/// <summary>
/// Centralizes all OpenTelemetry instrumentation (tracing and metrics) for the local transport.
/// Covers both the send (producer) and receive/process (consumer) sides.
/// </summary>
internal class LocalTelemetry(TimeProvider timeProvider)
{
    // ─── Send-side (producer) ──────────────────────────────────────

    /// <summary>
    /// Starts a "send local" activity and sets trace context on the message properties.
    /// </summary>
    public Activity? StartSendActivity(MessageProperties props, int contentLength)
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
    /// Records send metrics: operation duration and sent message count.
    /// </summary>
    public void RecordSent(long startTimestamp, Exception? sendException)
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

    /// <summary>
    /// Starts a "process local" activity, restoring trace context from the message properties.
    /// </summary>
    public Activity? StartConsumeActivity(MessageProperties props, int contentLength)
    {
        ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);

        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "process local",
            ActivityKind.Consumer,
            parentContext);

        if (activity != null)
        {
            activity.SetTag(MessagingSemanticConventions.OperationName, "process");
            activity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeProcess);
            activity.SetTag(MessagingSemanticConventions.System, "local");
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, contentLength);
        }

        return activity;
    }

    /// <summary>
    /// Records that a message was received: consumed message count and receive lag.
    /// </summary>
    public void RecordReceived(DateTimeOffset? messageTime, DateTimeOffset receivedTimestamp)
    {
        var tags = new TagList
        {
            { MessagingSemanticConventions.System, "local" },
            { MessagingSemanticConventions.OperationName, "process" },
            { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeProcess },
        };

        RatatoskrDiagnostics.ClientConsumedMessages.Add(1, tags);

        if (messageTime.HasValue)
        {
            var lag = Math.Max((receivedTimestamp - messageTime.Value).TotalSeconds, 0);
            RatatoskrDiagnostics.ReceiveLag.Record(lag, tags);
        }
    }

    /// <summary>
    /// Records that a message was processed: process duration and process lag.
    /// </summary>
    public void RecordProcessed(long startTimestamp, DateTimeOffset? messageTime, string? errorType)
    {
        var tags = new TagList
        {
            { MessagingSemanticConventions.System, "local" },
            { MessagingSemanticConventions.OperationName, "process" },
            { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeProcess },
        };

        if (errorType != null)
            tags.Add(MessagingSemanticConventions.ErrorType, errorType);

        RatatoskrDiagnostics.ProcessDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);

        if (messageTime.HasValue)
        {
            var lag = Math.Max((timeProvider.GetUtcNow() - messageTime.Value).TotalSeconds, 0);
            RatatoskrDiagnostics.ProcessLag.Record(lag, tags);
        }
    }
}
