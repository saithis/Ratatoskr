using System.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Centralizes all OpenTelemetry instrumentation (tracing and metrics) for the inbox processor.
/// </summary>
internal class InboxTelemetry
{
    /// <summary>
    /// Starts a "deliver inbox" activity, restoring trace context from the message properties.
    /// </summary>
    public Activity? StartDeliverActivity(MessageProperties props, string handlerKey)
    {
        ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);

        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "deliver inbox",
            ActivityKind.Consumer,
            parentContext
        );

        if (activity != null)
        {
            activity.SetTag(MessagingSemanticConventions.OperationName, "deliver");
            activity.SetTag(
                MessagingSemanticConventions.OperationType,
                MessagingSemanticConventions.OperationTypeProcess
            );
            activity.SetTag(MessagingSemanticConventions.System, "ratatoskr");
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag("ratatoskr.inbox.handler.key", handlerKey);
        }

        return activity;
    }

    public void RecordBatchSize(int count)
    {
        RatatoskrDiagnostics.InboxBatchSize.Record(count);
    }

    public void RecordDelivered(bool success)
    {
        RatatoskrDiagnostics.InboxDeliverCount.Add(
            1,
            new TagList { { "status", success ? "success" : "failure" } }
        );
    }

    public void RecordPoisoned()
    {
        RatatoskrDiagnostics.InboxPoisonCount.Add(1);
    }

    public void RecordBatchDuration(long startTimestamp)
    {
        RatatoskrDiagnostics.InboxProcessDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds
        );
    }
}
