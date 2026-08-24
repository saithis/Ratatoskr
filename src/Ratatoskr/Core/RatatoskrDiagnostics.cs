using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Ratatoskr.Core;

/// <summary>
/// Provides access to the ActivitySource used for OpenTelemetry tracing in Ratatoskr.
/// </summary>
public static class RatatoskrDiagnostics
{
    /// <summary>The name of the OpenTelemetry ActivitySource used for Ratatoskr tracing.</summary>
    public const string ActivitySourceName = "Ratatoskr";

    /// <summary>The name of the OpenTelemetry Meter used for Ratatoskr metrics.</summary>
    public const string MeterName = "Ratatoskr";

    private static readonly double[] _durationBuckets =
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
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = _durationBuckets }
    );

    /// <summary>Number of messages the producer attempted to send to the broker.</summary>
    public static readonly Counter<long> ClientSentMessages = Meter.CreateCounter<long>(
        "messaging.client.sent.messages",
        "{message}",
        "Number of messages producer attempted to send to the broker."
    );

    /// <summary>Number of messages delivered to the application.</summary>
    public static readonly Counter<long> ClientConsumedMessages = Meter.CreateCounter<long>(
        "messaging.client.consumed.messages",
        "{message}",
        "Number of messages that were delivered to the application."
    );

    /// <summary>Duration of message processing operations.</summary>
    public static readonly Histogram<double> ProcessDuration = Meter.CreateHistogram(
        "messaging.process.duration",
        "s",
        "Duration of processing operation.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = _durationBuckets }
    );

    /// <summary>
    /// Custom Ratatoskr metrics (no OTEL standard equivalent)
    /// </summary>
    /// <summary>Time from message creation to reception by this service.</summary>
    public static readonly Histogram<double> ReceiveLag = Meter.CreateHistogram<double>(
        "ratatoskr.receive.lag",
        "s",
        "Duration from message creation (sent) to reception."
    );

    /// <summary>Time from message creation to completion of all handler processing.</summary>
    public static readonly Histogram<double> ProcessLag = Meter.CreateHistogram<double>(
        "ratatoskr.process.lag",
        "s",
        "Duration from message creation (sent) to completion of processing."
    );

    /// <summary>
    /// Reliability Metrics
    /// </summary>
    /// <summary>Number of messages scheduled for retry after a processing failure.</summary>
    public static readonly Counter<long> RetryMessages = Meter.CreateCounter<long>(
        "ratatoskr.retry.messages",
        "{message}",
        "Number of messages scheduled for retry."
    );

    /// <summary>Number of messages moved to the dead-letter queue after exhausting retries.</summary>
    public static readonly Counter<long> DeadLetterMessages = Meter.CreateCounter<long>(
        "ratatoskr.dead_letter.messages",
        "{message}",
        "Number of messages sent to DLQ."
    );

    /// <summary>
    /// Outbox Metrics
    /// </summary>
    /// <summary>Number of messages successfully processed from the outbox.</summary>
    public static readonly Counter<long> OutboxProcessCount = Meter.CreateCounter<long>(
        "ratatoskr.outbox.process.count",
        "{message}",
        "Number of messages processed from the outbox."
    );

    /// <summary>Number of outbox messages marked as poisoned after repeated failures.</summary>
    public static readonly Counter<long> OutboxPoisonCount = Meter.CreateCounter<long>(
        "ratatoskr.outbox.poison.count",
        "{message}",
        "Number of outbox messages marked as poisoned."
    );

    /// <summary>Duration of a single outbox processing batch.</summary>
    public static readonly Histogram<double> OutboxProcessDuration = Meter.CreateHistogram<double>(
        "ratatoskr.outbox.process.duration",
        "s",
        "Duration of the outbox processing batch."
    );

    /// <summary>Number of messages picked up in a single outbox processing batch.</summary>
    public static readonly Histogram<long> OutboxBatchSize = Meter.CreateHistogram<long>(
        "ratatoskr.outbox.batch.size",
        "{message}",
        "Number of messages picked up in a batch."
    );

    /// <summary>
    /// Inbox Metrics
    /// </summary>
    /// <summary>Number of inbox handler deliveries attempted.</summary>
    public static readonly Counter<long> InboxDeliverCount = Meter.CreateCounter<long>(
        "ratatoskr.inbox.deliver.count",
        "{message}",
        "Number of inbox handler deliveries attempted."
    );

    /// <summary>Number of inbox handler statuses marked as poisoned after repeated failures.</summary>
    public static readonly Counter<long> InboxPoisonCount = Meter.CreateCounter<long>(
        "ratatoskr.inbox.poison.count",
        "{message}",
        "Number of inbox handler statuses marked as poisoned."
    );

    /// <summary>Duration of a single inbox processing batch.</summary>
    public static readonly Histogram<double> InboxProcessDuration = Meter.CreateHistogram<double>(
        "ratatoskr.inbox.process.duration",
        "s",
        "Duration of the inbox processing batch."
    );

    /// <summary>Number of inbox handler statuses picked up in a single processing batch.</summary>
    public static readonly Histogram<long> InboxBatchSize = Meter.CreateHistogram<long>(
        "ratatoskr.inbox.batch.size",
        "{message}",
        "Number of inbox handler statuses picked up in a batch."
    );

    /// <summary>
    /// Cleanup Metrics
    /// </summary>
    private static readonly double[] _cleanupDurationBuckets =
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

    /// <summary>Number of processed outbox messages deleted by the cleanup job.</summary>
    public static readonly Counter<long> OutboxCleanupCount = Meter.CreateCounter<long>(
        "ratatoskr.outbox.cleanup.count",
        "{message}",
        "Number of processed outbox messages deleted by cleanup."
    );

    /// <summary>Duration of an outbox cleanup operation.</summary>
    public static readonly Histogram<double> OutboxCleanupDuration = Meter.CreateHistogram(
        "ratatoskr.outbox.cleanup.duration",
        "s",
        "Duration of outbox cleanup operation.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = _cleanupDurationBuckets }
    );

    /// <summary>Number of completed inbox handler statuses deleted by the cleanup job.</summary>
    public static readonly Counter<long> InboxCleanupStatusCount = Meter.CreateCounter<long>(
        "ratatoskr.inbox.cleanup.status.count",
        "{status}",
        "Number of completed inbox handler statuses deleted by cleanup."
    );

    /// <summary>Number of orphaned inbox messages deleted by the cleanup job.</summary>
    public static readonly Counter<long> InboxCleanupMessageCount = Meter.CreateCounter<long>(
        "ratatoskr.inbox.cleanup.message.count",
        "{message}",
        "Number of orphaned inbox messages deleted by cleanup."
    );

    /// <summary>Duration of an inbox cleanup operation.</summary>
    public static readonly Histogram<double> InboxCleanupDuration = Meter.CreateHistogram(
        "ratatoskr.inbox.cleanup.duration",
        "s",
        "Duration of inbox cleanup operation.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = _cleanupDurationBuckets }
    );

    /// <summary>
    /// Distributed Lock Metrics
    /// </summary>
    /// <summary>Number of times a distributed lock could not be acquired.</summary>
    public static readonly Counter<long> LockAcquisitionFailure = Meter.CreateCounter<long>(
        "ratatoskr.lock.acquisition.failure",
        "{attempt}",
        "Number of times a distributed lock could not be acquired."
    );

    /// <summary>Number of times a distributed lock was lost unexpectedly during processing.</summary>
    public static readonly Counter<long> LockLost = Meter.CreateCounter<long>(
        "ratatoskr.lock.lost",
        "{event}",
        "Number of times a distributed lock was lost during processing."
    );
}
