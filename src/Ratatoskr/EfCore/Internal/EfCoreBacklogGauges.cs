using System.Diagnostics;
using System.Diagnostics.Metrics;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Registers all four EF Core backlog observable gauges once, so inbox-only and outbox-only apps
/// still expose stable metric names (disabled sides read as zero).
/// </summary>
internal sealed class EfCoreBacklogGauges
{
    public EfCoreBacklogGauges(EfCoreMetricsState state)
    {
        RatatoskrDiagnostics.Meter.CreateObservableGauge(
            "ratatoskr.outbox.pending.messages",
            () =>
                state.ContextMetrics.Select(kv => new Measurement<long>(
                    kv.Value.PendingOutboxCount,
                    new TagList { { "db_context", kv.Key } }
                )),
            description: "Number of pending outbox messages."
        );

        RatatoskrDiagnostics.Meter.CreateObservableGauge(
            "ratatoskr.outbox.poisoned.messages",
            () =>
                state.ContextMetrics.Select(kv => new Measurement<long>(
                    kv.Value.PoisonedOutboxCount,
                    new TagList { { "db_context", kv.Key } }
                )),
            description: "Number of outbox messages marked as poisoned."
        );

        RatatoskrDiagnostics.Meter.CreateObservableGauge(
            "ratatoskr.inbox.pending.statuses",
            () =>
                state.ContextMetrics.Select(kv => new Measurement<long>(
                    kv.Value.PendingInboxCount,
                    new TagList { { "db_context", kv.Key } }
                )),
            description: "Number of pending inbox handler statuses."
        );

        RatatoskrDiagnostics.Meter.CreateObservableGauge(
            "ratatoskr.inbox.poisoned.statuses",
            () =>
                state.ContextMetrics.Select(kv => new Measurement<long>(
                    kv.Value.PoisonedInboxCount,
                    new TagList { { "db_context", kv.Key } }
                )),
            description: "Number of inbox handler statuses marked as poisoned."
        );
    }
}
