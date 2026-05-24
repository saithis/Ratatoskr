using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public abstract class InboxTestBase(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    /// <summary>
    /// Waits for the expected number of inbox handler status entries to appear in the database.
    /// </summary>
    protected async Task WaitForInboxEntriesAsync(int expectedCount, TimeSpan? timeout = null)
    {
        await WaitForConditionAsync(
            async () =>
                await InScopeAsync(async ctx =>
                {
                    var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                    var count = await db.Set<InboxHandlerStatusEntity>().CountAsync();
                    return count >= expectedCount;
                }),
            timeout ?? TimeSpan.FromSeconds(10),
            $"Expected {expectedCount} inbox handler status entries to appear within timeout"
        );
    }

    protected async Task<int> ProcessInboxAsync(
        IServiceProvider serviceProvider,
        bool includeStuckDetection = true,
        CancellationToken cancellationToken = default
    )
    {
        var total = 0;
        while (true)
        {
            using var scope = serviceProvider.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<
                InboxMessageProcessor<TestDbContext>
            >();
            var count = await processor.ProcessBatchAsync(includeStuckDetection, cancellationToken);
            total += count;
            if (count == 0)
            {
                break;
            }
        }
        return total;
    }

    protected class InboxHandlerA : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct) =>
            Task.CompletedTask;
    }

    protected class InboxHandlerB : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct) =>
            Task.CompletedTask;
    }

    protected class AlwaysFailingHandler : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct) =>
            throw new InvalidOperationException("Handler failed intentionally");
    }

    protected class InvocationCounter
    {
        private int _count;

        public int Increment() => Interlocked.Increment(ref _count);

        public int Count => _count;
    }

    protected class CountingHandler(InvocationCounter counter) : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            counter.Increment();
            return Task.CompletedTask;
        }
    }

    protected class FailsThenSucceedsHandler(InvocationCounter counter) : IMessageHandler<TestEvent>
    {
        private const int FailuresBeforeSuccess = 2;

        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            var attempt = counter.Increment();
            if (attempt <= FailuresBeforeSuccess)
            {
                throw new InvalidOperationException($"Transient failure (attempt {attempt})");
            }

            return Task.CompletedTask;
        }
    }
}
