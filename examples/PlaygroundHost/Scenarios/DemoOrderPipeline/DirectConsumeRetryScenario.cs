using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class DirectConsumeRetryScenario : IScenario
{
    public string Slug => "direct-consume-retry";

    public string Title => "Notification OrderPlaced succeed-after-2";

    public string Description =>
        "Rabbit fan-out handler fails twice then succeeds; order still reaches Fulfilled.";

    public string Topic => "Direct consume";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        ScenarioToggleReset.ApplyBaseline(sp);
        sp.GetRequiredService<NotificationPlaygroundState>().ApplyOrderPlacedNotify("succeed-after", 2);
        try
        {
            var orderId = await OrderOutboxStaging.StageOutboxOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("notification_succeed_after_two");
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
            sp.GetRequiredService<NotificationPlaygroundState>().ApplyOrderPlacedNotify("succeed", null);
        }
    }
}
