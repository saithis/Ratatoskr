using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class BusinessRejectionScenario : IScenario
{
    public string Slug => "business-rejection";

    public string Title => "Business rejection (OrderFailed)";

    public string Description =>
        "Inventory reject mode stages OrderFailed; publisher marks the order Failed.";

    public string Topic => "Inbox";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        ScenarioToggleReset.ApplyBaseline(sp);
        sp.GetRequiredService<InventoryDemoModeState>().ApplyFromToggle("reject", null);
        try
        {
            var orderId = await OrderOutboxStaging.StageOutboxOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("inventory_reject_mode");
            return await ScenarioAssertions.OrderEventuallyAsync(
                context.ScopeFactory,
                orderId,
                OrderStatus.Failed,
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
