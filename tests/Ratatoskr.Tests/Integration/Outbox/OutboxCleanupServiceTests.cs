using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Integration.Outbox;

public class OutboxCleanupServiceTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : OutboxTestBase(rabbitMq, postgres)
{
    private readonly FakeTimeProvider _timeProvider = new(
        new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)
    );

    private OutboxCleanupService<TestDbContext> CreateCleanupService(OutboxOptions options) =>
        new(
            Services.GetRequiredService<IServiceScopeFactory>(),
            new OutboxOptionsHolder<TestDbContext>(options),
            Services.GetRequiredService<IDistributedLockProvider>(),
            _timeProvider,
            Services.GetRequiredService<ILogger<OutboxCleanupService<TestDbContext>>>()
        );

    private async Task SetupAsync()
    {
        await StartTestAsync(services =>
        {
            services.AddDbContext<TestDbContext>(
                (_, options) => options.UseNpgsql(PostgresConnectionString)
            );
        });
        await InitializeDatabase();
    }

    [Test]
    public async Task Cleanup_DeletesProcessedMessagesOlderThanRetention()
    {
        // Arrange
        await SetupAsync();
        var retentionPeriod = TimeSpan.FromDays(7);

        // Insert a processed message at "now"
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = OutboxMessageEntity.Create(
                "old"u8.ToArray(),
                new MessageProperties { Type = "test" },
                _timeProvider,
                "rabbitmq"
            );
            entity.MarkAsProcessing(_timeProvider);
            entity.MarkAsProcessed(_timeProvider);
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
        });

        // Advance time past retention
        _timeProvider.Advance(retentionPeriod + TimeSpan.FromHours(1));

        // Insert another processed message at "now" (within retention)
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = OutboxMessageEntity.Create(
                "new"u8.ToArray(),
                new MessageProperties { Type = "test" },
                _timeProvider,
                "rabbitmq"
            );
            entity.MarkAsProcessing(_timeProvider);
            entity.MarkAsProcessed(_timeProvider);
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
        });

        var options = new OutboxOptions { RetentionPeriod = retentionPeriod };
        var service = CreateCleanupService(options);

        // Act
        var deleted = await service.CleanupAsync(CancellationToken.None);

        // Assert
        deleted.Should().Be(1);
        var remaining = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<OutboxMessageEntity>().ToListAsync();
        });
        remaining.Should().HaveCount(1);
    }

    [Test]
    public async Task Cleanup_PreservesUnprocessedMessages()
    {
        // Arrange
        await SetupAsync();

        // Insert an unprocessed message
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = OutboxMessageEntity.Create(
                "pending"u8.ToArray(),
                new MessageProperties { Type = "test" },
                _timeProvider,
                "rabbitmq"
            );
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
        });

        // Advance time well past any retention period
        _timeProvider.Advance(TimeSpan.FromDays(365));

        var options = new OutboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        var service = CreateCleanupService(options);

        // Act
        var deleted = await service.CleanupAsync(CancellationToken.None);

        // Assert
        deleted.Should().Be(0);
        var remaining = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<OutboxMessageEntity>().CountAsync();
        });
        remaining.Should().Be(1);
    }

    [Test]
    public async Task Cleanup_PreservesPoisonedMessages()
    {
        // Arrange
        await SetupAsync();

        // Insert a poisoned message
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = OutboxMessageEntity.Create(
                "poisoned"u8.ToArray(),
                new MessageProperties { Type = "test" },
                _timeProvider,
                "rabbitmq"
            );
            entity.MarkAsPoisoned("test failure", _timeProvider);
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
        });

        // Advance time well past retention
        _timeProvider.Advance(TimeSpan.FromDays(365));

        var options = new OutboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        var service = CreateCleanupService(options);

        // Act
        var deleted = await service.CleanupAsync(CancellationToken.None);

        // Assert
        deleted.Should().Be(0);
        var remaining = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<OutboxMessageEntity>().CountAsync();
        });
        remaining.Should().Be(1);
    }

    [Test]
    public async Task Cleanup_RespectsCleanupBatchSize()
    {
        // Arrange
        await SetupAsync();

        // Insert 5 processed messages
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            for (var i = 0; i < 5; i++)
            {
                var entity = OutboxMessageEntity.Create(
                    "test"u8.ToArray(),
                    new MessageProperties { Type = "test" },
                    _timeProvider,
                    "rabbitmq"
                );
                entity.MarkAsProcessing(_timeProvider);
                entity.MarkAsProcessed(_timeProvider);
                db.Set<OutboxMessageEntity>().Add(entity);
            }
            await db.SaveChangesAsync();
        });

        // Advance past retention
        _timeProvider.Advance(TimeSpan.FromDays(10));

        // Use batch size of 2 - should still delete all 5 in multiple batches
        var options = new OutboxOptions
        {
            RetentionPeriod = TimeSpan.FromDays(1),
            CleanupBatchSize = 2,
        };
        var service = CreateCleanupService(options);

        // Act
        var deleted = await service.CleanupAsync(CancellationToken.None);

        // Assert
        deleted.Should().Be(5);
        var remaining = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<OutboxMessageEntity>().CountAsync();
        });
        remaining.Should().Be(0);
    }

    [Test]
    public async Task Cleanup_PreservesProcessedMessagesWithinRetention()
    {
        // Arrange
        await SetupAsync();

        // Insert a processed message at "now"
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = OutboxMessageEntity.Create(
                "recent"u8.ToArray(),
                new MessageProperties { Type = "test" },
                _timeProvider,
                "rabbitmq"
            );
            entity.MarkAsProcessing(_timeProvider);
            entity.MarkAsProcessed(_timeProvider);
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
        });

        // Advance time but NOT past retention
        _timeProvider.Advance(TimeSpan.FromDays(3));

        var options = new OutboxOptions { RetentionPeriod = TimeSpan.FromDays(7) };
        var service = CreateCleanupService(options);

        // Act
        var deleted = await service.CleanupAsync(CancellationToken.None);

        // Assert
        deleted.Should().Be(0);
    }

    [Test]
    public async Task Cleanup_RecordsMetrics()
    {
        // Arrange
        await SetupAsync();

        long cleanupCount = 0;
        double cleanupDuration = -1;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == RatatoskrDiagnostics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, _, _) =>
            {
                if (instrument.Name == "ratatoskr.outbox.cleanup.count")
                {
                    Interlocked.Add(ref cleanupCount, measurement);
                }
            }
        );
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, _, _) =>
            {
                if (instrument.Name == "ratatoskr.outbox.cleanup.duration")
                {
                    cleanupDuration = measurement;
                }
            }
        );
        listener.Start();

        // Insert a processed message
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = OutboxMessageEntity.Create(
                "test"u8.ToArray(),
                new MessageProperties { Type = "test" },
                _timeProvider,
                "rabbitmq"
            );
            entity.MarkAsProcessing(_timeProvider);
            entity.MarkAsProcessed(_timeProvider);
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
        });

        _timeProvider.Advance(TimeSpan.FromDays(10));

        var options = new OutboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        var service = CreateCleanupService(options);

        // Act
        await service.CleanupAsync(CancellationToken.None);
        listener.RecordObservableInstruments();

        // Assert — use >= 1 since static counters may accumulate from parallel tests
        cleanupCount.Should().BeGreaterThanOrEqualTo(1);
        cleanupDuration.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task TryCleanupWithLock_AcquiresLockAndCleansUp()
    {
        // Arrange
        await SetupAsync();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = OutboxMessageEntity.Create(
                "test"u8.ToArray(),
                new MessageProperties { Type = "test" },
                _timeProvider,
                "rabbitmq"
            );
            entity.MarkAsProcessing(_timeProvider);
            entity.MarkAsProcessed(_timeProvider);
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
        });

        _timeProvider.Advance(TimeSpan.FromDays(10));

        var options = new OutboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        var service = CreateCleanupService(options);

        // Act
        var acquired = await service.TryCleanupWithLockAsync(CancellationToken.None);

        // Assert
        acquired.Should().BeTrue();
        var remaining = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<OutboxMessageEntity>().CountAsync();
        });
        remaining.Should().Be(0);
    }

    [Test]
    public async Task TryCleanupWithLock_SkipsWhenLockIsHeld()
    {
        // Arrange
        await SetupAsync();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = OutboxMessageEntity.Create(
                "test"u8.ToArray(),
                new MessageProperties { Type = "test" },
                _timeProvider,
                "rabbitmq"
            );
            entity.MarkAsProcessing(_timeProvider);
            entity.MarkAsProcessed(_timeProvider);
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
        });

        _timeProvider.Advance(TimeSpan.FromDays(10));

        var options = new OutboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        var service = CreateCleanupService(options);

        // Hold the lock externally
        var lockProvider = Services.GetRequiredService<IDistributedLockProvider>();
        await using var heldLock = await lockProvider.AcquireLockAsync(options.CleanupLockName);

        // Act
        var acquired = await service.TryCleanupWithLockAsync(CancellationToken.None);

        // Assert — lock not acquired, cleanup skipped
        acquired.Should().BeFalse();
        var remaining = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<OutboxMessageEntity>().CountAsync();
        });
        remaining.Should().Be(1);
    }
}
