using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class OutboxRetryThenSuccessScenario : IScenario
{
    public string Slug => "outbox-retry-then-success";

    public string Title => "Outbox relay retries then succeeds";

    public string Description =>
        "Simulates transport failures on the publisher outbox send path, then succeeds; order still reaches Fulfilled.";

    public string Topic => "Outbox";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        ScenarioToggleReset.ApplyBaseline(sp);
        var outboxFail = sp.GetRequiredService<OutboxFailureState>();
        outboxFail.SetActiveScenarioRun(runId);
        outboxFail.Apply("succeed-after", 2);
        try
        {
            var orderId = await OrderOutboxStaging.StageOutboxOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("staged_with_send_failures");
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
            outboxFail.Apply("succeed", null);
            outboxFail.SetActiveScenarioRun(null);
        }
    }
}
