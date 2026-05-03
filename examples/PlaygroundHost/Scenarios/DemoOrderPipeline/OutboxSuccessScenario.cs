using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class OutboxSuccessScenario : IScenario
{
    public string Slug => "outbox-success";

    public string Title => "Outbox happy path";

    public string Description =>
        "Creates an order on the publisher database with outbox staging; consumer fulfills; publisher reaches Fulfilled.";

    public string Topic => "Outbox";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var time = setup.ServiceProvider.GetRequiredService<TimeProvider>();
        var db = setup.ServiceProvider.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        ScenarioToggleReset.ApplyBaseline(setup.ServiceProvider);
        var orderId = await OrderOutboxStaging.StageOutboxOrderAsync(db, time, runId, cancellationToken);
        context.StepsCompleted.Add("order_persisted_outbox_staged");

        return await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Fulfilled,
            TimeSpan.FromSeconds(60),
            time,
            cancellationToken);
    }
}
