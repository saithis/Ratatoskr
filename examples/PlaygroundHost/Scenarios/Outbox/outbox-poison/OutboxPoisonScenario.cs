using Microsoft.Extensions.DependencyInjection;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using Ratatoskr;
using Ratatoskr.RabbitMq.Extensions;

namespace PlaygroundHost.Scenarios.Outbox.OutboxPoison;

/// <summary>Forces publisher outbox sends to fail until the staged message becomes poisoned for this run.</summary>
public sealed class OutboxPoisonScenario : IPlaygroundScenario
{
    private const string ScenarioSlug = "outbox-poison";

    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues => [];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus)
    {
        var exEvt = PlaygroundAmqpNames.EventsExchange(ScenarioSlug);
        bus.AddEventPublishChannel(exEvt, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<PoisonProbe>());
    }

    public string Slug => ScenarioSlug;

    public string Title => "Outbox poisoned rows";

    public string Description =>
        "Forces publisher outbox transport sends to fail until messages become poisoned for this run.";

    public string Topic => "Outbox";

    private async Task StagePoisonProbeAsync(
        PublisherDbContext context,
        string runId,
        CancellationToken cancellationToken)
    {
        this.StageCorrelatedOutboxMessage(
            context,
            runId,
            new PoisonProbe(runId),
            $"outbox-poison-{runId}");
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var runId = context.ScenarioRunId;
        var registry = context.GetRequired<OutboxSendFailureRegistry>();
        registry.Register(runId, OutboxSendFailureKind.AlwaysFail, 0);
        try
        {
            var before = await PlaygroundSqlMetrics.CountPoisonedOutboxForScenarioRunAsync(context.PublisherDb, runId, cancellationToken);
            await StagePoisonProbeAsync(context.PublisherDb, runId, cancellationToken);
            context.StepsCompleted.Add("staged_always_fail_send");

            return await ScenarioAssertions.IntMetricEventuallyExceedsBaselineAsync(
                context.TimeProvider,
                ScenarioTiming.PollLoopLong,
                ScenarioTiming.PollIntervalSlow,
                before,
                async ct =>
                {
                    await using var scope2 = context.ScopeFactory.CreateAsyncScope();
                    var db2 = scope2.ServiceProvider.GetRequiredService<PublisherDbContext>();
                    return await PlaygroundSqlMetrics.CountPoisonedOutboxForScenarioRunAsync(db2, runId, ct);
                },
                "Poisoned outbox count",
                cancellationToken);
        }
        finally
        {
            registry.Unregister(runId);
        }
    }

    [RatatoskrMessage("outbox-poison.probe")]
    public sealed record PoisonProbe(string ScenarioRunId);
}
