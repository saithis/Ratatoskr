using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Outbox;

public class OutboxClaimConcurrencyTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : OutboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Outbox_ClaimConflictWithoutEntries_DoesNotSpinForever()
    {
        // Reproduces the claim-loop infinite spin: MarkMessagesAsProcessingAsync retries
        // SaveChanges in a while(true) loop, only terminating when the conflicting set
        // shrinks. A DbUpdateConcurrencyException whose Entries collection is empty (the
        // same "conflict on a different entity" scenario the processor already handles in
        // ProcessMessageAsync) never removes a message from the set, so the loop spins
        // forever while holding the distributed lock -> total processing halt.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        // Throws on up to 50 claim saves, then lets the save through. Buggy code keeps
        // retrying the unchanged set and only escapes after the 51st attempt; correct code
        // must stop after the first non-reducing conflict.
        var interceptor = new ClaimConflictInterceptor(maxThrows: 50);
        var sender = new SucceedingSender(RabbitMqConstants.TransportName);

        await StartTestAsync(services =>
        {
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
            });
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();
            services.AddSingleton(new OutboxOptionsHolder<TestDbContext>(new OutboxOptions()));
            services.AddSingleton(interceptor);
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                    options.AddInterceptors(sp.GetRequiredService<ClaimConflictInterceptor>());
                }
            );
            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(sender);
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "claim-spin-msg" });
            await dbContext.SaveChangesAsync(); // staging save (Added entity, not a claim)
        });

        await InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<
                OutboxMessageProcessor<TestDbContext>
            >();

            // With the bug this either spins (bounded here to 50 throws) and reports
            // 51 claim attempts, or rethrows after a single attempt with the fix.
            try
            {
                await processor.ProcessBatchAsync(
                    includeStuckMessageDetection: false,
                    CancellationToken.None
                );
            }
            catch (DbUpdateConcurrencyException)
            {
                // Expected with the fix: a non-reducing conflict is rethrown so the batch
                // is retried fresh on the next cycle instead of spinning.
            }
        });

        interceptor
            .ClaimSaveAttempts.Should()
            .Be(
                1,
                "the claim-save retry loop must not re-attempt an unchanged set on a conflict "
                    + "that does not identify any claimed message (otherwise it spins forever)"
            );
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
                    entry.Entity is OutboxMessageEntity
                    {
                        ProcessingStartedAt: not null,
                        ProcessedAt: null
                    }
                )
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class SucceedingSender(string transportName) : IMessageSender
    {
        public string TransportName => transportName;

        public Task SendAsync(
            byte[] content,
            MessageProperties props,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
