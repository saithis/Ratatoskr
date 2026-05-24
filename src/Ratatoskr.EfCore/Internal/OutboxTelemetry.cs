using System.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Centralizes all OpenTelemetry instrumentation (tracing and metrics) for the outbox processor.
/// </summary>
internal class OutboxTelemetry
{
    /// <summary>
    /// Starts a "create outbox" activity, restoring trace context from the message properties.
    /// </summary>
    public static Activity? StartCreateActivity(MessageProperties props)
    {
        // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/
        ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);

        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "create outbox",
            ActivityKind.Producer,
            parentContext
        );

        if (activity != null)
        {
            activity.SetTag(MessagingSemanticConventions.OperationName, "create");
            activity.SetTag(
                MessagingSemanticConventions.OperationType,
                MessagingSemanticConventions.OperationTypeCreate
            );
            activity.SetTag(MessagingSemanticConventions.System, "ratatoskr");
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
        }

        return activity;
    }

    public static void RecordBatchSize(int count)
    {
        RatatoskrDiagnostics.OutboxBatchSize.Record(count);
    }

    public static void RecordProcessed(bool success)
    {
        RatatoskrDiagnostics.OutboxProcessCount.Add(
            1,
            new TagList { { "status", success ? "success" : "failure" } }
        );
    }

    public static void RecordPoisoned()
    {
        RatatoskrDiagnostics.OutboxPoisonCount.Add(1);
    }

    public static void RecordBatchDuration(long startTimestamp)
    {
        RatatoskrDiagnostics.OutboxProcessDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds
        );
    }
}
