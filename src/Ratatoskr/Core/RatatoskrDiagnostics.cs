using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Ratatoskr.Core;

/// <summary>
/// Provides access to the ActivitySource used for OpenTelemetry tracing in Ratatoskr.
/// </summary>
public static class RatatoskrDiagnostics
{
    public const string ActivitySourceName = "Ratatoskr";
    public const string MeterName = "Ratatoskr";

    private static readonly double[] DurationBuckets =
    [
        0.005,
        0.01,
        0.025,
        0.05,
        0.075,
        0.1,
        0.25,
        0.5,
        0.75,
        1.0,
        2.5,
        5.0,
        7.5,
        10.0,
    ];

    /// <summary>
    /// The ActivitySource for Ratatoskr.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// The Meter for Ratatoskr metrics.
    /// </summary>
    public static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Standard OTEL Messaging Metrics
    /// </summary>
    public static readonly Histogram<double> ClientOperationDuration = Meter.CreateHistogram(
        "messaging.client.operation.duration",
        "s",
        "Duration of messaging operation initiated by a producer or consumer client.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets }
    );

    public static readonly Counter<long> ClientSentMessages = Meter.CreateCounter<long>(
        "messaging.client.sent.messages",
        "{message}",
        "Number of messages producer attempted to send to the broker."
    );

    public static readonly Counter<long> ClientConsumedMessages = Meter.CreateCounter<long>(
        "messaging.client.consumed.messages",
        "{message}",
        "Number of messages that were delivered to the application."
    );

    public static readonly Histogram<double> ProcessDuration = Meter.CreateHistogram(
        "messaging.process.duration",
        "s",
        "Duration of processing operation.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets }
    );

    /// <summary>
    /// Custom Ratatoskr metrics (no OTEL standard equivalent)
    /// </summary>
    public static readonly Histogram<double> ReceiveLag = Meter.CreateHistogram<double>(
        "ratatoskr.receive.lag",
        "s",
        "Duration from message creation (sent) to reception."
    );
    public static readonly Histogram<double> ProcessLag = Meter.CreateHistogram<double>(
        "ratatoskr.process.lag",
        "s",
        "Duration from message creation (sent) to completion of processing."
    );

    /// <summary>
    /// Reliability Metrics
    /// </summary>
    public static readonly Counter<long> RetryMessages = Meter.CreateCounter<long>(
        "ratatoskr.retry.messages",
        "{message}",
        "Number of messages scheduled for retry."
    );
    public static readonly Counter<long> DeadLetterMessages = Meter.CreateCounter<long>(
        "ratatoskr.dead_letter.messages",
        "{message}",
        "Number of messages sent to DLQ."
    );

    /// <summary>
    /// Outbox Metrics
    /// </summary>
    public static readonly Counter<long> OutboxProcessCount = Meter.CreateCounter<long>(
        "ratatoskr.outbox.process.count",
        "{message}",
        "Number of messages processed from the outbox."
    );
    public static readonly Counter<long> OutboxPoisonCount = Meter.CreateCounter<long>(
        "ratatoskr.outbox.poison.count",
        "{message}",
        "Number of outbox messages marked as poisoned."
    );
    public static readonly Histogram<double> OutboxProcessDuration = Meter.CreateHistogram<double>(
        "ratatoskr.outbox.process.duration",
        "s",
        "Duration of the outbox processing batch."
    );
    public static readonly Histogram<long> OutboxBatchSize = Meter.CreateHistogram<long>(
        "ratatoskr.outbox.batch.size",
        "{message}",
        "Number of messages picked up in a batch."
    );

    /// <summary>
    /// Inbox Metrics
    /// </summary>
    public static readonly Counter<long> InboxDeliverCount = Meter.CreateCounter<long>(
        "ratatoskr.inbox.deliver.count",
        "{message}",
        "Number of inbox handler deliveries attempted."
    );
    public static readonly Counter<long> InboxPoisonCount = Meter.CreateCounter<long>(
        "ratatoskr.inbox.poison.count",
        "{message}",
        "Number of inbox handler statuses marked as poisoned."
    );
    public static readonly Histogram<double> InboxProcessDuration = Meter.CreateHistogram<double>(
        "ratatoskr.inbox.process.duration",
        "s",
        "Duration of the inbox processing batch."
    );
    public static readonly Histogram<long> InboxBatchSize = Meter.CreateHistogram<long>(
        "ratatoskr.inbox.batch.size",
        "{message}",
        "Number of inbox handler statuses picked up in a batch."
    );

    /// <summary>
    /// Cleanup Metrics
    /// </summary>
    private static readonly double[] CleanupDurationBuckets =
    [
        0.1,
        0.5,
        1.0,
        5.0,
        10.0,
        30.0,
        60.0,
        120.0,
    ];

    public static readonly Counter<long> OutboxCleanupCount = Meter.CreateCounter<long>(
        "ratatoskr.outbox.cleanup.count",
        "{message}",
        "Number of processed outbox messages deleted by cleanup."
    );
    public static readonly Histogram<double> OutboxCleanupDuration = Meter.CreateHistogram(
        "ratatoskr.outbox.cleanup.duration",
        "s",
        "Duration of outbox cleanup operation.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = CleanupDurationBuckets }
    );
    public static readonly Counter<long> InboxCleanupStatusCount = Meter.CreateCounter<long>(
        "ratatoskr.inbox.cleanup.status.count",
        "{status}",
        "Number of completed inbox handler statuses deleted by cleanup."
    );
    public static readonly Counter<long> InboxCleanupMessageCount = Meter.CreateCounter<long>(
        "ratatoskr.inbox.cleanup.message.count",
        "{message}",
        "Number of orphaned inbox messages deleted by cleanup."
    );
    public static readonly Histogram<double> InboxCleanupDuration = Meter.CreateHistogram(
        "ratatoskr.inbox.cleanup.duration",
        "s",
        "Duration of inbox cleanup operation.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = CleanupDurationBuckets }
    );

    /// <summary>
    /// Distributed Lock Metrics
    /// </summary>
    public static readonly Counter<long> LockAcquisitionFailure = Meter.CreateCounter<long>(
        "ratatoskr.lock.acquisition.failure",
        "{attempt}",
        "Number of times a distributed lock could not be acquired."
    );
    public static readonly Counter<long> LockLost = Meter.CreateCounter<long>(
        "ratatoskr.lock.lost",
        "{event}",
        "Number of times a distributed lock was lost during processing."
    );
}
