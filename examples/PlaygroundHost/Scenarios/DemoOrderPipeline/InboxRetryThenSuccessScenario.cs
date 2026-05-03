using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class InboxRetryThenSuccessScenario : IScenario
{
    public string Slug => "inbox-retry-then-success";

    public string Title => "Inventory inbox retry then success";

    public string Description =>
        "ProcessOrderCommand fails twice in inventory inbox simulation, then succeeds; order reaches Fulfilled.";

    public string Topic => "Inbox";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        ScenarioToggleReset.ApplyBaseline(sp);
        sp.GetRequiredService<InventoryDemoModeState>().ApplyFromToggle("succeed-after", 2);
        try
        {
            var orderId = await OrderOutboxStaging.StageOutboxOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("inventory_succeed_after_two_failures");
            return await ScenarioAssertions.OrderEventuallyAsync(
                context.ScopeFactory,
                orderId,
                OrderStatus.Fulfilled,
                TimeSpan.FromSeconds(90),
                time,
                cancellationToken);
        }
        finally
        {
            sp.GetRequiredService<InventoryDemoModeState>().SetMode(InventoryDemoMode.Off);
        }
    }
}
