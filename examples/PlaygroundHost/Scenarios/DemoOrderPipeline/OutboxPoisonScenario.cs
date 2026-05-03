using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class OutboxPoisonScenario : IScenario
{
    public string Slug => "outbox-poison";

    public string Title => "Outbox poisoned rows";

    public string Description =>
        "Forces publisher outbox transport sends to fail until messages become poisoned; expects poisoned outbox count to increase.";

    public string Topic => "Outbox";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        ScenarioToggleReset.ApplyBaseline(sp);
        var before = await PlaygroundSqlMetrics.CountPoisonedOutboxAsync(db, cancellationToken);
        var outboxFail = sp.GetRequiredService<OutboxFailureState>();
        outboxFail.SetActiveScenarioRun(runId);
        outboxFail.Apply("fail", null);
        try
        {
            _ = await OrderOutboxStaging.StageOutboxOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("staged_always_fail_send");

            var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(90);
            while (time.GetUtcNow() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var scope2 = context.ScopeFactory.CreateAsyncScope();
                var db2 = scope2.ServiceProvider.GetRequiredService<PublisherDbContext>();
                var after = await PlaygroundSqlMetrics.CountPoisonedOutboxAsync(db2, cancellationToken);
                if (after > before)
                    return new ScenarioVerdict(true, details: new { before, after });

                await Task.Delay(800, cancellationToken);
            }

            var final = await PlaygroundSqlMetrics.CountPoisonedOutboxAsync(db, cancellationToken);
            return new ScenarioVerdict(false, $"Poisoned outbox count did not increase within timeout (before={before}, after={final}).");
        }
        finally
        {
            outboxFail.Apply("succeed", null);
            outboxFail.SetActiveScenarioRun(null);
        }
    }
}
