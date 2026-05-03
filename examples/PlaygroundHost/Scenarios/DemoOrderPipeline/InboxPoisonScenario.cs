using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class InboxPoisonScenario : IScenario
{
    public string Slug => "inbox-poison";

    public string Title => "Inventory inbox poison";

    public string Description =>
        "Inventory command handler throw mode until a poisoned inbox row appears on the consumer database.";

    public string Topic => "Inbox";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var pub = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        ScenarioToggleReset.ApplyBaseline(sp);
        int before;
        await using (var conScope = context.ScopeFactory.CreateAsyncScope())
        {
            var conDb = conScope.ServiceProvider.GetRequiredService<ConsumerDbContext>();
            before = await PlaygroundSqlMetrics.CountPoisonedInboxAsync(conDb, cancellationToken);
        }
        sp.GetRequiredService<InventoryDemoModeState>().ApplyFromToggle("throw", null);
        try
        {
            _ = await OrderOutboxStaging.StageOutboxOrderAsync(pub, time, runId, cancellationToken);
            context.StepsCompleted.Add("inventory_throw_mode");

            var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(90);
            while (time.GetUtcNow() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var scope2 = context.ScopeFactory.CreateAsyncScope();
                var db2 = scope2.ServiceProvider.GetRequiredService<ConsumerDbContext>();
                var after = await PlaygroundSqlMetrics.CountPoisonedInboxAsync(db2, cancellationToken);
                if (after > before)
                    return new ScenarioVerdict(true, details: new { before, after });

                await Task.Delay(800, cancellationToken);
            }

            await using var conFinal = context.ScopeFactory.CreateAsyncScope();
            var conDbFinal = conFinal.ServiceProvider.GetRequiredService<ConsumerDbContext>();
            var final = await PlaygroundSqlMetrics.CountPoisonedInboxAsync(conDbFinal, cancellationToken);
            return new ScenarioVerdict(false, $"Poisoned consumer inbox count did not increase (before={before}, after={final}).");
        }
        finally
        {
            sp.GetRequiredService<InventoryDemoModeState>().SetMode(InventoryDemoMode.Off);
        }
    }
}
