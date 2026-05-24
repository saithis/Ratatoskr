using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public class InboxScopeAndIsolationTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Inbox_HandlerScopeIsolation_HandlersDoNotShareDbContext()
    {
        var tracker = new ScopeIsolationTracker();
        await StartTestAsync(services =>
        {
            services.AddSingleton(tracker);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "test-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "test-events",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<ChangeTrackerPollutingHandler>("polluting-handler")
                                    .WithHandler<ChangeTrackerCheckingHandler>("checking-handler")
                            )
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "scope-isolation-1" },
                new MessageProperties { Id = "scope-isolation-1" }
            );
        });

        await tracker.WaitForBothHandlersAsync(TimeSpan.FromSeconds(10));

        tracker.DbContextIds.Should().HaveCount(2, "both handlers should have run");
        tracker
            .DbContextIds.Values.Distinct()
            .Should()
            .HaveCount(2, "each handler should receive its own DbContext instance");
        tracker
            .CheckingHandlerSawChanges.Should()
            .BeFalse(
                "handlers should have isolated DI scopes — ChangeTrackerCheckingHandler should not see ChangeTrackerPollutingHandler's tracked entities"
            );
    }

    [Test]
    public async Task Inbox_PerHandlerSave_CompletedHandlersSurviveSubsequentFailures()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "inbox-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "inbox-events",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<InboxHandlerA>("a-succeeds")
                                    .WithHandler<AlwaysFailingHandler>("b-fails")
                            )
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(inbox => inbox.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-persist-1" },
                new MessageProperties { Id = "persist-1" }
            );
        });

        await WaitForInboxEntriesAsync(2);

        await InScopeAsync(async ctx => await ProcessInboxAsync(ctx.ServiceProvider));

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var succeeding = await db.Set<InboxHandlerStatusEntity>()
                .SingleAsync(s => s.HandlerKey == "a-succeeds");
            succeeding.CompletedAt.Should().NotBeNull();
            succeeding.ErrorCount.Should().Be(0);

            var failing = await db.Set<InboxHandlerStatusEntity>()
                .SingleAsync(s => s.HandlerKey == "b-fails");
            failing.CompletedAt.Should().BeNull();
            failing.ErrorCount.Should().Be(1);
        });
    }

    private class ChangeTrackerPollutingHandler(TestDbContext db, ScopeIsolationTracker tracker)
        : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            db.TestEntities.Add(new TestEntity { Name = "leaked-from-polluting-handler" });
            tracker.RecordHandler("polluting", db.GetHashCode());
            return Task.CompletedTask;
        }
    }

    private class ChangeTrackerCheckingHandler(TestDbContext db, ScopeIsolationTracker tracker)
        : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties props, CancellationToken ct)
        {
            tracker.CheckingHandlerSawChanges = db.ChangeTracker.HasChanges();
            tracker.RecordHandler("checking", db.GetHashCode());
            return Task.CompletedTask;
        }
    }

    private class ScopeIsolationTracker
    {
        private readonly TaskCompletionSource _allDone = new();
        private int _handlerCount;
        public bool CheckingHandlerSawChanges { get; set; }
        public ConcurrentDictionary<string, int> DbContextIds { get; } = new();

        public void RecordHandler(string name, int dbContextId)
        {
            DbContextIds[name] = dbContextId;
            if (Interlocked.Increment(ref _handlerCount) >= 2)
            {
                _allDone.TrySetResult();
            }
        }

        public Task WaitForBothHandlersAsync(TimeSpan timeout) => _allDone.Task.WaitAsync(timeout);
    }
}
