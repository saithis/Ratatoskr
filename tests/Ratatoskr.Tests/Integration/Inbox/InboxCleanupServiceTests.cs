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

namespace Ratatoskr.Tests.Integration.Inbox;

public class InboxCleanupServiceTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : InboxTestBase(rabbitMq, postgres)
{
    private readonly FakeTimeProvider _timeProvider = new(
        new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)
    );

    private InboxCleanupService<TestDbContext> CreateCleanupService(InboxOptions options) =>
        new(
            Services.GetRequiredService<IServiceScopeFactory>(),
            new InboxOptionsHolder<TestDbContext>(options),
            Services.GetRequiredService<IDistributedLockProvider>(),
            _timeProvider,
            Services.GetRequiredService<ILogger<InboxCleanupService<TestDbContext>>>()
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

    private InboxMessageEntity CreateInboxMessage(string id) =>
        InboxMessageEntity.Create(
            id,
            "rabbitmq",
            "test"u8.ToArray(),
            new MessageProperties { Type = "test" },
            _timeProvider
        );

    [Test]
    public async Task Cleanup_DeletesCompletedHandlerStatusesOlderThanRetention()
    {
        // Arrange
        await SetupAsync();
        var retentionPeriod = TimeSpan.FromDays(30);

        // Insert a message with a completed handler status
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = CreateInboxMessage("msg-old");
            db.Set<InboxMessageEntity>().Add(message);
            var status = InboxHandlerStatusEntity.Create("msg-old", "handler-a", _timeProvider);
            status.MarkAsProcessing(_timeProvider);
            status.MarkAsCompleted(_timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(status);
            await db.SaveChangesAsync();
        });

        // Advance past retention
        _timeProvider.Advance(retentionPeriod + TimeSpan.FromHours(1));

        // Insert a new completed handler status (within retention)
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = CreateInboxMessage("msg-new");
            db.Set<InboxMessageEntity>().Add(message);
            var status = InboxHandlerStatusEntity.Create("msg-new", "handler-a", _timeProvider);
            status.MarkAsProcessing(_timeProvider);
            status.MarkAsCompleted(_timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(status);
            await db.SaveChangesAsync();
        });

        var options = new InboxOptions { RetentionPeriod = retentionPeriod };
        using var service = CreateCleanupService(options);

        // Act
        var (handlerStatuses, orphanedMessages) = await service.CleanupAsync(
            CancellationToken.None
        );

        // Assert
        handlerStatuses.Should().Be(1);
        orphanedMessages.Should().Be(1); // The old message is now orphaned

        var remainingStatuses = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<InboxHandlerStatusEntity>().CountAsync();
        });
        remainingStatuses.Should().Be(1);

        var remainingMessages = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<InboxMessageEntity>().CountAsync();
        });
        remainingMessages.Should().Be(1);
    }

    [Test]
    public async Task Cleanup_PreservesPoisonedHandlerStatuses()
    {
        // Arrange
        await SetupAsync();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = CreateInboxMessage("msg-poisoned");
            db.Set<InboxMessageEntity>().Add(message);
            var status = InboxHandlerStatusEntity.Create(
                "msg-poisoned",
                "handler-a",
                _timeProvider
            );
            status.MarkAsPoisoned("test failure", _timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(status);
            await db.SaveChangesAsync();
        });

        // Advance well past retention
        _timeProvider.Advance(TimeSpan.FromDays(365));

        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        using var service = CreateCleanupService(options);

        // Act
        var (handlerStatuses, orphanedMessages) = await service.CleanupAsync(
            CancellationToken.None
        );

        // Assert
        handlerStatuses.Should().Be(0);
        orphanedMessages.Should().Be(0); // Message still has a handler status (poisoned)
    }

    [Test]
    public async Task Cleanup_PreservesPendingHandlerStatuses()
    {
        // Arrange
        await SetupAsync();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = CreateInboxMessage("msg-pending");
            db.Set<InboxMessageEntity>().Add(message);
            var status = InboxHandlerStatusEntity.Create("msg-pending", "handler-a", _timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(status);
            await db.SaveChangesAsync();
        });

        // Advance well past retention
        _timeProvider.Advance(TimeSpan.FromDays(365));

        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        using var service = CreateCleanupService(options);

        // Act
        var (handlerStatuses, orphanedMessages) = await service.CleanupAsync(
            CancellationToken.None
        );

        // Assert
        handlerStatuses.Should().Be(0);
        orphanedMessages.Should().Be(0);
    }

    [Test]
    public async Task Cleanup_PreservesCompletedStatusesWithinRetention()
    {
        // Arrange
        await SetupAsync();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = CreateInboxMessage("msg-recent");
            db.Set<InboxMessageEntity>().Add(message);
            var status = InboxHandlerStatusEntity.Create("msg-recent", "handler-a", _timeProvider);
            status.MarkAsProcessing(_timeProvider);
            status.MarkAsCompleted(_timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(status);
            await db.SaveChangesAsync();
        });

        // Advance but NOT past retention
        _timeProvider.Advance(TimeSpan.FromDays(3));

        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(30) };
        using var service = CreateCleanupService(options);

        // Act
        var (handlerStatuses, _) = await service.CleanupAsync(CancellationToken.None);

        // Assert
        handlerStatuses.Should().Be(0);
    }

    [Test]
    public async Task Cleanup_DeletesOrphanedMessagesAfterStatusCleanup()
    {
        // Arrange
        await SetupAsync();

        // Insert a message with two handler statuses: one completed (old), one completed (old)
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = CreateInboxMessage("msg-multi");
            db.Set<InboxMessageEntity>().Add(message);

            var statusA = InboxHandlerStatusEntity.Create("msg-multi", "handler-a", _timeProvider);
            statusA.MarkAsProcessing(_timeProvider);
            statusA.MarkAsCompleted(_timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(statusA);

            var statusB = InboxHandlerStatusEntity.Create("msg-multi", "handler-b", _timeProvider);
            statusB.MarkAsProcessing(_timeProvider);
            statusB.MarkAsCompleted(_timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(statusB);

            await db.SaveChangesAsync();
        });

        // Advance past retention
        _timeProvider.Advance(TimeSpan.FromDays(10));

        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        using var service = CreateCleanupService(options);

        // Act
        var (handlerStatuses, orphanedMessages) = await service.CleanupAsync(
            CancellationToken.None
        );

        // Assert
        handlerStatuses.Should().Be(2);
        orphanedMessages.Should().Be(1);

        var remainingMessages = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<InboxMessageEntity>().CountAsync();
        });
        remainingMessages.Should().Be(0);
    }

    [Test]
    public async Task Cleanup_RespectsCleanupBatchSize()
    {
        // Arrange
        await SetupAsync();

        // Insert 5 messages each with a completed handler status
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            for (var i = 0; i < 5; i++)
            {
                var message = CreateInboxMessage(
                    $"msg-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                );
                db.Set<InboxMessageEntity>().Add(message);
                var status = InboxHandlerStatusEntity.Create(
                    $"msg-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    "handler-a",
                    _timeProvider
                );
                status.MarkAsProcessing(_timeProvider);
                status.MarkAsCompleted(_timeProvider);
                db.Set<InboxHandlerStatusEntity>().Add(status);
            }
            await db.SaveChangesAsync();
        });

        // Advance past retention
        _timeProvider.Advance(TimeSpan.FromDays(10));

        // Use batch size of 2 — should still delete all 5 in multiple batches
        var options = new InboxOptions
        {
            RetentionPeriod = TimeSpan.FromDays(1),
            CleanupBatchSize = 2,
        };
        using var service = CreateCleanupService(options);

        // Act
        var (handlerStatuses, orphanedMessages) = await service.CleanupAsync(
            CancellationToken.None
        );

        // Assert
        handlerStatuses.Should().Be(5);
        orphanedMessages.Should().Be(5);

        var remainingStatuses = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<InboxHandlerStatusEntity>().CountAsync();
        });
        remainingStatuses.Should().Be(0);

        var remainingMessages = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<InboxMessageEntity>().CountAsync();
        });
        remainingMessages.Should().Be(0);
    }

    [Test]
    public async Task Cleanup_OrphanedMessages_DeletesAllAcrossMultipleBatches()
    {
        // Arrange — verifies deterministic batching with OrderBy works across multiple loop iterations
        await SetupAsync();

        // Insert 3 orphaned messages (no handler statuses) at different times
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Set<InboxMessageEntity>().Add(CreateInboxMessage("msg-oldest"));
            await db.SaveChangesAsync();
        });

        _timeProvider.Advance(TimeSpan.FromHours(1));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Set<InboxMessageEntity>().Add(CreateInboxMessage("msg-middle"));
            await db.SaveChangesAsync();
        });

        _timeProvider.Advance(TimeSpan.FromHours(1));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Set<InboxMessageEntity>().Add(CreateInboxMessage("msg-newest"));
            await db.SaveChangesAsync();
        });

        // Batch size 1 forces 3 loop iterations — deterministic ordering prevents
        // non-deterministic Take() from causing repeated work or skipped rows
        var options = new InboxOptions
        {
            RetentionPeriod = TimeSpan.FromDays(1),
            CleanupBatchSize = 1,
        };
        using var service = CreateCleanupService(options);

        // Act
        var (_, orphanedMessages) = await service.CleanupAsync(CancellationToken.None);

        // Assert — all 3 orphans deleted across 3 batches
        orphanedMessages.Should().Be(3);

        var remainingMessages = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<InboxMessageEntity>().CountAsync();
        });
        remainingMessages.Should().Be(0);
    }

    [Test]
    public async Task Cleanup_KeepsMessageWhenSomeStatusesRemain()
    {
        // Arrange
        await SetupAsync();

        // Insert a message with one completed (old) and one pending handler status
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = CreateInboxMessage("msg-partial");
            db.Set<InboxMessageEntity>().Add(message);

            var completedStatus = InboxHandlerStatusEntity.Create(
                "msg-partial",
                "handler-a",
                _timeProvider
            );
            completedStatus.MarkAsProcessing(_timeProvider);
            completedStatus.MarkAsCompleted(_timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(completedStatus);

            var pendingStatus = InboxHandlerStatusEntity.Create(
                "msg-partial",
                "handler-b",
                _timeProvider
            );
            db.Set<InboxHandlerStatusEntity>().Add(pendingStatus);

            await db.SaveChangesAsync();
        });

        // Advance past retention
        _timeProvider.Advance(TimeSpan.FromDays(10));

        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        using var service = CreateCleanupService(options);

        // Act
        var (handlerStatuses, orphanedMessages) = await service.CleanupAsync(
            CancellationToken.None
        );

        // Assert
        handlerStatuses.Should().Be(1); // Only completed status deleted
        orphanedMessages.Should().Be(0); // Message still has a pending handler status

        var remainingMessages = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<InboxMessageEntity>().CountAsync();
        });
        remainingMessages.Should().Be(1);
    }

    [Test]
    public async Task Cleanup_RecordsMetrics()
    {
        // Arrange
        await SetupAsync();

        long statusCleanupCount = 0;
        long messageCleanupCount = 0;
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
                if (instrument.Name == "ratatoskr.inbox.cleanup.status.count")
                {
                    Interlocked.Add(ref statusCleanupCount, measurement);
                }

                if (instrument.Name == "ratatoskr.inbox.cleanup.message.count")
                {
                    Interlocked.Add(ref messageCleanupCount, measurement);
                }
            }
        );
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, _, _) =>
            {
                if (instrument.Name == "ratatoskr.inbox.cleanup.duration")
                {
                    cleanupDuration = measurement;
                }
            }
        );
        listener.Start();

        // Insert a message with a completed handler status
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = CreateInboxMessage("msg-metrics");
            db.Set<InboxMessageEntity>().Add(message);
            var status = InboxHandlerStatusEntity.Create("msg-metrics", "handler-a", _timeProvider);
            status.MarkAsProcessing(_timeProvider);
            status.MarkAsCompleted(_timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(status);
            await db.SaveChangesAsync();
        });

        _timeProvider.Advance(TimeSpan.FromDays(10));

        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        using var service = CreateCleanupService(options);

        // Act
        await service.CleanupAsync(CancellationToken.None);
        listener.RecordObservableInstruments();

        // Assert — use >= 1 since static counters may accumulate from parallel tests
        statusCleanupCount.Should().BeGreaterThanOrEqualTo(1);
        messageCleanupCount.Should().BeGreaterThanOrEqualTo(1);
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
            var message = CreateInboxMessage("msg-lock-test");
            db.Set<InboxMessageEntity>().Add(message);
            var status = InboxHandlerStatusEntity.Create(
                "msg-lock-test",
                "handler-a",
                _timeProvider
            );
            status.MarkAsProcessing(_timeProvider);
            status.MarkAsCompleted(_timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(status);
            await db.SaveChangesAsync();
        });

        _timeProvider.Advance(TimeSpan.FromDays(10));

        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        using var service = CreateCleanupService(options);

        // Act
        var acquired = await service.TryCleanupWithLockAsync(CancellationToken.None);

        // Assert
        acquired.Should().BeTrue();
        var remaining = await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            return await db.Set<InboxHandlerStatusEntity>().CountAsync();
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
            var message = CreateInboxMessage("msg-lock-skip");
            db.Set<InboxMessageEntity>().Add(message);
            var status = InboxHandlerStatusEntity.Create(
                "msg-lock-skip",
                "handler-a",
                _timeProvider
            );
            status.MarkAsProcessing(_timeProvider);
            status.MarkAsCompleted(_timeProvider);
            db.Set<InboxHandlerStatusEntity>().Add(status);
            await db.SaveChangesAsync();
        });

        _timeProvider.Advance(TimeSpan.FromDays(10));

        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        using var service = CreateCleanupService(options);

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
            return await db.Set<InboxHandlerStatusEntity>().CountAsync();
        });
        remaining.Should().Be(1);
    }
}
