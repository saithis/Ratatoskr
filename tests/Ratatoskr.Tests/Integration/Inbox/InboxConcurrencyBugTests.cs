using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Inbox;

public class InboxConcurrencyBugTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Inbox_ClaimConflictWithoutEntries_DoesNotSpinForever()
    {
        // Mirror of the outbox claim-loop spin: MarkStatusesAsProcessingAsync retries
        // SaveChanges in a while(true) loop that only terminates when the conflicting set
        // shrinks. A DbUpdateConcurrencyException with an empty Entries collection never
        // removes a status, so the loop spins forever holding the distributed lock.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var interceptor = new ClaimConflictInterceptor(maxThrows: 50);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddSingleton(interceptor);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "inbox-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "inbox-events",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("claim-spin"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(inbox => inbox.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) =>
                {
                    opts.UseNpgsql(PostgresConnectionString);
                    opts.AddInterceptors(sp.GetRequiredService<ClaimConflictInterceptor>());
                }
            );
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-claim-spin-1" },
                new MessageProperties { Id = "claim-spin-1" }
            );
        });

        await WaitForInboxEntriesAsync(1);

        await InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<
                InboxMessageProcessor<TestDbContext>
            >();
            try
            {
                await processor.ProcessBatchAsync(
                    includeStuckMessageDetection: false,
                    CancellationToken.None
                );
            }
            catch (DbUpdateConcurrencyException)
            {
                // Expected with the fix.
            }
        });

        interceptor
            .ClaimSaveAttempts.Should()
            .Be(
                1,
                "the inbox claim-save retry loop must not re-attempt an unchanged set on a "
                    + "non-reducing conflict (otherwise it spins forever)"
            );
    }

    [Test]
    public async Task Inbox_CompletionSaveConflict_RemainingBatchStillProcessed()
    {
        // The per-status completion save (InboxMessageProcessor.ProcessStatusAsync) has no
        // DbUpdateConcurrencyException handling, unlike the outbox's ProcessMessageAsync.
        // A conflict while persisting one status's completion throws out of the batch loop,
        // abandoning every still-unprocessed claimed status -> they stay claimed and are
        // stuck until the StuckMessageThreshold elapses.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var interceptor = new FirstCompletionConflictInterceptor();
        var counter = new InvocationCounter();

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddSingleton(interceptor);
            services.AddSingleton(counter);
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "inbox-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "inbox-events",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<CountingHandler>("counting"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(inbox => inbox.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) =>
                {
                    opts.UseNpgsql(PostgresConnectionString);
                    opts.AddInterceptors(
                        sp.GetRequiredService<FirstCompletionConflictInterceptor>()
                    );
                }
            );
        });

        await InitializeDatabase();

        // Two messages -> two handler statuses, claimed together in one batch.
        // Batch order is by MessageId ascending, so "msg-a" is processed before "msg-b".
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-a" },
                new MessageProperties { Id = "msg-a" }
            );
            await bus.PublishDirectAsync(
                new TestEvent { Id = "business-b" },
                new MessageProperties { Id = "msg-b" }
            );
        });

        await WaitForInboxEntriesAsync(2);

        await InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<
                InboxMessageProcessor<TestDbContext>
            >();
            try
            {
                await processor.ProcessBatchAsync(
                    includeStuckMessageDetection: false,
                    CancellationToken.None
                );
            }
            catch (DbUpdateConcurrencyException)
            {
                // With the bug the conflict on msg-a's completion save escapes here.
            }
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();

            var msgB = statuses.Single(s => s.MessageId == "msg-b");
            msgB.CompletedAt.Should()
                .NotBeNull(
                    "a conflict on an earlier status's completion save must not abandon the "
                        + "rest of the already-claimed batch"
                );
        });
    }

    private sealed class ClaimConflictInterceptor(int maxThrows) : SaveChangesInterceptor
    {
        public int ClaimSaveAttempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (eventData.Context != null && IsClaimSave(eventData.Context))
            {
                ClaimSaveAttempts++;
                if (ClaimSaveAttempts <= maxThrows)
                {
                    throw new DbUpdateConcurrencyException(
                        "Simulated concurrency conflict with no entries",
                        (Exception?)null
                    );
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static bool IsClaimSave(DbContext context)
        {
            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State != EntityState.Modified)
                {
                    continue;
                }

                if (
                    entry.Entity is InboxHandlerStatusEntity
                    {
                        ProcessingStartedAt: not null,
                        CompletedAt: null
                    }
                )
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class FirstCompletionConflictInterceptor : SaveChangesInterceptor
    {
        private bool _thrown;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (!_thrown && eventData.Context != null && IsCompletionSave(eventData.Context))
            {
                _thrown = true;
                throw new DbUpdateConcurrencyException(
                    "Simulated concurrency conflict with no entries",
                    (Exception?)null
                );
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static bool IsCompletionSave(DbContext context)
        {
            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State != EntityState.Modified)
                {
                    continue;
                }

                if (entry.Entity is InboxHandlerStatusEntity { CompletedAt: not null })
                {
                    return true;
                }
            }

            return false;
        }
    }
}
