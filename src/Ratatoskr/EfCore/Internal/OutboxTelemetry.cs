using System.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Centralizes all OpenTelemetry instrumentation (tracing and metrics) for the outbox processor.
/// </summary>
internal static class OutboxTelemetry
{
    /// <summary>
    /// Starts a "create outbox" activity, restoring trace context from the message properties.
    /// </summary>
    public static Activity? StartCreateActivity(MessageProperties props)
    {
        // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/
#pragma warning disable MA0060
        ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);
#pragma warning restore MA0060

        return RatatoskrDiagnostics.StartMessagingActivity(
            "create outbox",
            ActivityKind.Producer,
            "create",
            MessagingSemanticConventions.OperationTypeCreate,
            props.Id,
            parentContext
        );
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
