using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.EfCore;

public class EfCoreMetricsBackgroundServiceTests
{
    [Test]
    public async Task UpdateMetricsAsync_AccuratelyCountsPendingAndPoisonedMessages()
    {
        // Arrange
        var services = new ServiceCollection();
        var state = new EfCoreMetricsState();
        var timeProvider = new FakeTimeProvider();
        // Scoped DbContextOptions re-run the configure delegate per scope; use one name, not Guid.NewGuid() inside the lambda.
        var inMemoryDatabaseName = Guid.NewGuid().ToString();
        services.AddDbContext<TestDbContext>(options =>
            options.UseInMemoryDatabase(inMemoryDatabaseName)
        );

        // We simulate that both outbox and inbox are enabled
        services.AddSingleton(new OutboxOptionsHolder<TestDbContext>(new OutboxOptions()));
        services.AddSingleton(new InboxOptionsHolder<TestDbContext>(new InboxOptions()));
        services.AddSingleton(state);
        services.AddSingleton(
            new EfCoreMetricsSettings<TestDbContext>(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5)
            )
        );
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton<ILogger<EfCoreMetricsBackgroundService<TestDbContext>>>(
            NullLogger<EfCoreMetricsBackgroundService<TestDbContext>>.Instance
        );
        services.AddHostedService<EfCoreMetricsBackgroundService<TestDbContext>>();

        await using var provider = services.BuildServiceProvider();
        // Seed with a scoped DbContext so the in-memory store matches what UpdateMetricsAsync sees (scoped resolution).
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<TestDbContext>();
            await dbContext.Database.EnsureCreatedAsync();

            // Add Outbox Messages
            // 2 pending
            var outPending1 = OutboxMessageEntity.Create(
                [1, 2, 3],
                new MessageProperties { Id = Guid.NewGuid().ToString() },
                timeProvider,
                "test"
            );
            var outPending2 = OutboxMessageEntity.Create(
                [1, 2, 3],
                new MessageProperties { Id = Guid.NewGuid().ToString() },
                timeProvider,
                "test"
            );
            // 1 poisoned
            var outPoisoned = OutboxMessageEntity.Create(
                [1, 2, 3],
                new MessageProperties { Id = Guid.NewGuid().ToString() },
                timeProvider,
                "test"
            );
            outPoisoned.MarkAsPoisoned("error", timeProvider);
            // 1 processed (should not be counted)
            var outProcessed = OutboxMessageEntity.Create(
                [1, 2, 3],
                new MessageProperties { Id = Guid.NewGuid().ToString() },
                timeProvider,
                "test"
            );
            outProcessed.MarkAsProcessed(timeProvider);

            await dbContext
                .Set<OutboxMessageEntity>()
                .AddRangeAsync(outPending1, outPending2, outPoisoned, outProcessed);

            // Add Inbox Statuses
            // 3 pending
            var inPending1 = InboxHandlerStatusEntity.Create(
                Guid.NewGuid().ToString(),
                "handler1",
                timeProvider
            );
            var inPending2 = InboxHandlerStatusEntity.Create(
                Guid.NewGuid().ToString(),
                "handler2",
                timeProvider
            );
            var inPending3 = InboxHandlerStatusEntity.Create(
                Guid.NewGuid().ToString(),
                "handler1",
                timeProvider
            );
            // 2 poisoned
            var inPoisoned1 = InboxHandlerStatusEntity.Create(
                Guid.NewGuid().ToString(),
                "handler1",
                timeProvider
            );
            inPoisoned1.MarkAsPoisoned("error");
            var inPoisoned2 = InboxHandlerStatusEntity.Create(
                Guid.NewGuid().ToString(),
                "handler2",
                timeProvider
            );
            inPoisoned2.MarkAsPoisoned("error");
            // 1 completed (should not be counted)
            var inCompleted = InboxHandlerStatusEntity.Create(
                Guid.NewGuid().ToString(),
                "handler1",
                timeProvider
            );
            inCompleted.MarkAsCompleted(timeProvider);

            await dbContext
                .Set<InboxHandlerStatusEntity>()
                .AddRangeAsync(
                    inPending1,
                    inPending2,
                    inPending3,
                    inPoisoned1,
                    inPoisoned2,
                    inCompleted
                );
            await dbContext.SaveChangesAsync();
        }

        var backgroundService = provider
            .GetServices<IHostedService>()
            .OfType<EfCoreMetricsBackgroundService<TestDbContext>>()
            .First();

        // Act
        await backgroundService.UpdateMetricsAsync(CancellationToken.None);

        // Assert
        var metrics = state.ContextMetrics[typeof(TestDbContext).FullName!];
        metrics.Should().NotBeNull();

        metrics.PendingOutboxCount.Should().Be(2);
        metrics.PoisonedOutboxCount.Should().Be(1);
        metrics.PendingInboxCount.Should().Be(3);
        metrics.PoisonedInboxCount.Should().Be(2);
    }
}
