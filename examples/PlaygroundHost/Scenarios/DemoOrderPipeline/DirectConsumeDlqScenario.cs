using System.Linq;
using Microsoft.Extensions.Configuration;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class DirectConsumeDlqScenario(IConfiguration configuration) : IScenario
{
    public string Slug => "direct-consume-dlq";

    public string Title => "Notification DLQ (no inbox)";

    public string Description =>
        "OrderPlaced notify handler always fails; expect notifications main queue DLQ depth to grow.";

    public string Topic => "Direct consume";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        var rabbitCs = configuration.GetConnectionString("rabbitmq")
            ?? throw new InvalidOperationException("rabbitmq connection string missing.");
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var runId = context.ScenarioRunId;
        ScenarioToggleReset.ApplyBaseline(sp);
        var mainQ = PlaygroundRabbitQueues.ConsumerQueues.First(q => q.Key == "notifications").MainQueueName;
        var d0 = await RabbitDlqDepthReader.GetDlqCountAsync(rabbitCs, mainQ, cancellationToken);
        sp.GetRequiredService<NotificationPlaygroundState>().ApplyOrderPlacedNotify("fail", null);
        try
        {
            _ = await OrderOutboxStaging.StageOutboxOrderAsync(db, time, runId, cancellationToken);
            context.StepsCompleted.Add("notification_always_fail");

            var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(90);
            while (time.GetUtcNow() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var d = await RabbitDlqDepthReader.GetDlqCountAsync(rabbitCs, mainQ, cancellationToken);
                if (d > d0)
                    return new ScenarioVerdict(true, details: new { before = d0, after = d });

                await Task.Delay(1000, cancellationToken);
            }

            var final = await RabbitDlqDepthReader.GetDlqCountAsync(rabbitCs, mainQ, cancellationToken);
            return new ScenarioVerdict(false, $"DLQ depth did not increase (before={d0}, after={final}).");
        }
        finally
        {
            sp.GetRequiredService<NotificationPlaygroundState>().ApplyOrderPlacedNotify("succeed", null);
        }
    }
}
