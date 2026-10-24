using System.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Centralizes all OpenTelemetry instrumentation (tracing and metrics) for the inbox processor.
/// </summary>
internal static class InboxTelemetry
{
    /// <summary>
    /// Starts a "deliver inbox" activity, restoring trace context from the message properties.
    /// </summary>
    public static Activity? StartDeliverActivity(MessageProperties props, string handlerKey)
    {
#pragma warning disable MA0060
        ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);
#pragma warning restore MA0060

        var activity = RatatoskrDiagnostics.StartMessagingActivity(
            "deliver inbox",
            ActivityKind.Consumer,
            "deliver",
            MessagingSemanticConventions.OperationTypeProcess,
            props.Id,
            parentContext
        );
        activity?.SetTag("ratatoskr.inbox.handler.key", handlerKey);
        return activity;
    }

    public static void RecordBatchSize(int count)
    {
        RatatoskrDiagnostics.InboxBatchSize.Record(count);
    }

    public static void RecordDelivered(bool success)
    {
        RatatoskrDiagnostics.InboxDeliverCount.Add(
            1,
            new TagList { { "status", success ? "success" : "failure" } }
        );
    }

    public static void RecordPoisoned()
    {
        RatatoskrDiagnostics.InboxPoisonCount.Add(1);
    }

    public static void RecordBatchDuration(long startTimestamp)
    {
        RatatoskrDiagnostics.InboxProcessDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds
        );
    }
}
