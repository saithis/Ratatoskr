using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Outbox;

public abstract class OutboxTestBase(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    protected string ExchangeName => $"outbox-test-{TestId}";
    protected string QueueName => $"outbox-queue-{TestId}";
    protected static string DefaultRoutingKey => "test.event";

    protected async Task<BasicGetResult?> WaitForMessageAsync(
        string queueName,
        TimeSpan? timeout = null
    )
    {
        BasicGetResult? result = null;
        await WaitForConditionAsync(
            async () =>
            {
                result = await GetMessageAsync(queueName);
                return result != null;
            },
            timeout ?? TimeSpan.FromSeconds(10)
        );
        return result;
    }

    protected static async Task<int> ProcessOutboxAsync<TDbContext>(
        IServiceProvider serviceProvider
    )
        where TDbContext : DbContext, IOutboxDbContext
    {
        var totalProcessed = 0;
        while (true)
        {
            using var scope = serviceProvider.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<
                OutboxMessageProcessor<TDbContext>
            >();
            var batchProcessed = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                CancellationToken.None
            );
            totalProcessed += batchProcessed;
            if (batchProcessed == 0)
            {
                break;
            }
        }
        return totalProcessed;
    }
}
