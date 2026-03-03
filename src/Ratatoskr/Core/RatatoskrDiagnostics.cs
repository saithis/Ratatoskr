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
        [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1.0, 2.5, 5.0, 7.5, 10.0];

    /// <summary>
    /// The ActivitySource for Ratatoskr.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// The Meter for Ratatoskr metrics.
    /// </summary>
    public static readonly Meter Meter = new(MeterName);

    // Standard OTEL Messaging Metrics
    public static readonly Histogram<double> ClientOperationDuration = Meter.CreateHistogram<double>(
        "messaging.client.operation.duration", "s",
        "Duration of messaging operation initiated by a producer or consumer client.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });

    public static readonly Counter<long> ClientSentMessages = Meter.CreateCounter<long>(
        "messaging.client.sent.messages", "{message}",
        "Number of messages producer attempted to send to the broker.");

    public static readonly Counter<long> ClientConsumedMessages = Meter.CreateCounter<long>(
        "messaging.client.consumed.messages", "{message}",
        "Number of messages that were delivered to the application.");

    public static readonly Histogram<double> ProcessDuration = Meter.CreateHistogram<double>(
        "messaging.process.duration", "s",
        "Duration of processing operation.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });

    // Custom Ratatoskr metrics (no OTEL standard equivalent)
    public static readonly Histogram<double> ReceiveLag = Meter.CreateHistogram<double>("ratatoskr.receive.lag", "s", "Duration from message creation (sent) to reception.");
    public static readonly Histogram<double> ProcessLag = Meter.CreateHistogram<double>("ratatoskr.process.lag", "s", "Duration from message creation (sent) to completion of processing.");

    // Reliability Metrics
    public static readonly Counter<long> RetryMessages = Meter.CreateCounter<long>("ratatoskr.retry.messages", "{message}", "Number of messages scheduled for retry.");
    public static readonly Counter<long> DeadLetterMessages = Meter.CreateCounter<long>("ratatoskr.dead_letter.messages", "{message}", "Number of messages sent to DLQ.");

    // Outbox Metrics
    public static readonly Counter<long> OutboxProcessCount = Meter.CreateCounter<long>("ratatoskr.outbox.process.count", "{message}", "Number of messages processed from the outbox.");
    public static readonly Histogram<double> OutboxProcessDuration = Meter.CreateHistogram<double>("ratatoskr.outbox.process.duration", "s", "Duration of the outbox processing batch.");
    public static readonly Histogram<long> OutboxBatchSize = Meter.CreateHistogram<long>("ratatoskr.outbox.batch.size", "{message}", "Number of messages picked up in a batch.");

    // Inbox Metrics
    public static readonly Counter<long> InboxDeliverCount = Meter.CreateCounter<long>("ratatoskr.inbox.deliver.count", "{message}", "Number of inbox handler deliveries attempted.");
    public static readonly Counter<long> InboxPoisonCount = Meter.CreateCounter<long>("ratatoskr.inbox.poison.count", "{message}", "Number of inbox handler statuses marked as poisoned.");
    public static readonly Histogram<double> InboxProcessDuration = Meter.CreateHistogram<double>("ratatoskr.inbox.process.duration", "s", "Duration of the inbox processing batch.");
    public static readonly Histogram<long> InboxBatchSize = Meter.CreateHistogram<long>("ratatoskr.inbox.batch.size", "{message}", "Number of inbox handler statuses picked up in a batch.");

    // Cleanup Metrics
    public static readonly Counter<long> InboxCleanupCount = Meter.CreateCounter<long>("ratatoskr.inbox.cleanup.count", "{message}", "Number of inbox messages deleted by cleanup.");
    public static readonly Counter<long> OutboxCleanupCount = Meter.CreateCounter<long>("ratatoskr.outbox.cleanup.count", "{message}", "Number of outbox messages deleted by cleanup.");

    // Distributed Lock Metrics
    public static readonly Counter<long> LockAcquisitionFailure = Meter.CreateCounter<long>("ratatoskr.lock.acquisition.failure", "{attempt}", "Number of times a distributed lock could not be acquired.");
    public static readonly Counter<long> LockLost = Meter.CreateCounter<long>("ratatoskr.lock.lost", "{event}", "Number of times a distributed lock was lost during processing.");
}
