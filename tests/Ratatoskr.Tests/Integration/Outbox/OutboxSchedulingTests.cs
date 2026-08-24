using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Outbox;

public class OutboxSchedulingTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : OutboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Outbox_ScheduledInFuture_IgnoredByProcessorUntilTimeElapses()
    {
        var startTime = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(startTime);

        // Arrange
        await StartTestAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(timeProvider));
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c =>
                        c.WithRabbitMq(r => r.WithTopicExchange())
                            .Produces<TestEvent>(m => m.WithRoutingKey(DefaultRoutingKey))
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(o => o.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        var deliverAt = startTime.AddHours(2);

        // Stage message scheduled for 2 hours in the future
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(
                new TestEvent { Id = "sched-1", Data = "delayed" },
                options => options.DeliverAt(deliverAt)
            );
            await dbContext.SaveChangesAsync();
        });

        // Verify entity persisted with ScheduledAt
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().SingleAsync();
            entity.ScheduledAt.Should().Be(deliverAt);
            entity.ProcessedAt.Should().BeNull();
        });

        // Act 1: Process batch before scheduled time (at startTime) -> 0 messages processed
        await InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<OutboxMessageProcessor<TestDbContext>>();
            var processed = await processor.ProcessBatchAsync(includeStuckMessageDetection: false, CancellationToken.None);
            processed.Should().Be(0);
        });

        // Verify entity is still unprocessed
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().SingleAsync();
            entity.ProcessedAt.Should().BeNull();
        });

        // Advance time to 2 hours later
        timeProvider.Advance(TimeSpan.FromHours(2));

        // Act 2: Process batch after scheduled time -> message is processed
        await InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<OutboxMessageProcessor<TestDbContext>>();
            var processed = await processor.ProcessBatchAsync(includeStuckMessageDetection: false, CancellationToken.None);
            processed.Should().Be(1);
        });

        // Verify entity is now processed
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().SingleAsync();
            entity.ProcessedAt.Should().NotBeNull();
        });
    }

    [Test]
    public async Task Outbox_DeliverAfter_SetsScheduledAtCorrectly()
    {
        var startTime = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(startTime);

        // Arrange
        await StartTestAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(timeProvider));
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(o => o.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        var delay = TimeSpan.FromMinutes(30);

        // Act
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(
                new TestEvent { Id = "sched-2", Data = "deliver-after" },
                options => options.DeliverAfter(delay, timeProvider)
            );
            await dbContext.SaveChangesAsync();
        });

        // Assert
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().SingleAsync();
            entity.ScheduledAt.Should().Be(startTime.Add(delay));
        });
    }

    [Test]
    public async Task DirectPublishAsync_WithScheduledAt_WaitsUntilScheduledTime()
    {
        var startTime = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(startTime);

        await StartTestAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(timeProvider));
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
            });
        });

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            var delay = TimeSpan.FromHours(1);

            // Act - Start publishing with future delivery time
            var publishTask = bus.PublishDirectAsync(
                new TestEvent { Id = "direct-sched-1" },
                options => options.DeliverAfter(delay, timeProvider)
            );

            publishTask.IsCompleted.Should().BeFalse();

            // Advance time past delay
            timeProvider.Advance(TimeSpan.FromHours(1.1));

            await publishTask;
            publishTask.IsCompletedSuccessfully.Should().BeTrue();
        });
    }
}
