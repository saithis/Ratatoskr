using System.Diagnostics.CodeAnalysis;
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

public class OutboxDurabilityTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : OutboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Outbox_MultipleDbContextsInParallel_Isolated()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c =>
                        c.WithRabbitMq(r => r.WithTopicExchange())
                            .Produces<TestEvent>(m => m.WithRoutingKey(DefaultRoutingKey))
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
        await InitializeDatabase();

        // Act - Two parallel scopes stage different messages
        var task1 = InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "scope-1-msg" });
            await dbContext.SaveChangesAsync();
        });

        var task2 = InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "scope-2-msg" });
            await dbContext.SaveChangesAsync();
        });

        await Task.WhenAll(task1, task2);

        // Assert - Wait for both messages to be delivered
        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(QueueName) >= 2,
            TimeSpan.FromSeconds(10)
        );

        // Verify both are marked as processed
        // Note: ProcessedAt is persisted AFTER messages are sent to RabbitMQ,
        // so we need to wait for the database to be updated too.
        await WaitForConditionAsync(
            async () =>
                await InScopeAsync(async ctx =>
                {
                    var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                    var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
                    return entities.Count == 2 && entities.TrueForAll(e => e.ProcessedAt != null);
                }),
            TimeSpan.FromSeconds(10)
        );
    }

    [Test]
    public async Task Outbox_PerMessageSave_ProcessedMessageSurvivesNextFailure()
    {
        // Verifies the per-message save fix: when message 1 sends successfully and message 2 fails,
        // message 1's ProcessedAt is already persisted to the database.
        // Before the fix (batch save), both would be lost if a crash occurred after send but before SaveChanges.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sender = new SucceedThenFailSender(
            RabbitMqConstants.TransportName,
            successesBeforeFailure: 1
        );

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
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(sender);
        });

        await InitializeDatabase();

        // Stage 2 messages
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "msg-1" });
            dbContext.OutboxMessages.Add(new TestEvent { Data = "msg-2" });
            await dbContext.SaveChangesAsync();
        });

        // Act — first message succeeds, second fails
        await InScopeAsync(async ctx =>
        {
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert — first message should be processed, second should have error
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entities = await dbContext
                .Set<OutboxMessageEntity>()
                .OrderBy(e => e.CreatedAt)
                .ToListAsync();

            entities.Should().HaveCount(2);
            entities[0]
                .ProcessedAt.Should()
                .NotBeNull("first message should be persisted as processed");
            entities[1]
                .ProcessedAt.Should()
                .BeNull("second message failed and should not be processed");
            entities[1].ErrorCount.Should().Be(1);
        });
    }

    [Test]
    public async Task Outbox_SendTimeout_IncreasesErrorCount()
    {
        // Verifies that a send operation exceeding the configured timeout is treated as a failure.
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var slowSender = new SlowMessageSender(RabbitMqConstants.TransportName);

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
            services.AddSingleton(
                new OutboxOptionsHolder<TestDbContext>(
                    new OutboxOptions { SendTimeout = TimeSpan.FromMilliseconds(100) }
                )
            );
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(slowSender);
        });

        await InitializeDatabase();

        // Stage a message
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "slow-msg" });
            await dbContext.SaveChangesAsync();
        });

        // Act — process; the slow sender will be cancelled by the timeout
        await InScopeAsync(async ctx =>
        {
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert — message should have failed, not processed
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();
            entity
                .ProcessedAt.Should()
                .BeNull("send timed out and should not be marked as processed");
            entity.ErrorCount.Should().Be(1);
            entity.IsPoisoned.Should().BeFalse();
        });
    }

    [Test]
    public async Task Outbox_StuckMessageDetection_ReprocessesStuckMessage()
    {
        // Verifies that a message stuck in "processing" state is recovered after the threshold.
        var startTime = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(startTime);

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
            services.AddSingleton(
                new OutboxOptionsHolder<TestDbContext>(
                    new OutboxOptions { StuckMessageThreshold = TimeSpan.FromMinutes(5) }
                )
            );
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        // Stage a message and simulate it getting stuck in "processing"
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "stuck-msg" });
            await dbContext.SaveChangesAsync();
        });

        // Process normally — marks as processing and sends
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();
            entity.MarkAsProcessing(fakeTime);
            await dbContext.SaveChangesAsync();
        });

        // Advance 1 minute — below the 5-minute threshold
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        // Process with stuck detection — too recent, should NOT pick up
        await InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<
                OutboxMessageProcessor<TestDbContext>
            >();

            var count = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                CancellationToken.None
            );
            count.Should().Be(0, "message is stuck for only 1 minute — not yet considered stuck");
        });

        // Advance to 6 minutes total — past the 5-minute threshold
        fakeTime.Advance(TimeSpan.FromMinutes(5));

        // Process with stuck detection — should pick up and re-process
        await InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<
                OutboxMessageProcessor<TestDbContext>
            >();

            var count = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                CancellationToken.None
            );
            count.Should().BeGreaterThan(0);
        });

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();
            entity.ProcessedAt.Should().NotBeNull("stuck message should have been re-processed");
        });
    }

    [Test]
    [SuppressMessage(
        "Usage",
        "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "Needed for this test"
    )]
    public async Task Outbox_CallerCancellation_PropagatesInsteadOfRecordingFailure()
    {
        // Verifies that when the caller's cancellation token fires during send,
        // the OperationCanceledException propagates (not treated as a transport failure).
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero));

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
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
            services.RemoveAll<IMessageSender>();
            services.AddSingleton(new BlockingMessageSender(RabbitMqConstants.TransportName));
            services.AddSingleton<IMessageSender>(sp =>
                sp.GetRequiredService<BlockingMessageSender>()
            );
        });

        await InitializeDatabase();
        var blockingSender = Services.GetRequiredService<BlockingMessageSender>();

        // Stage a message
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "cancel-msg" });
            await dbContext.SaveChangesAsync();
        });

        // Act — cancel the caller token while the sender is blocked
        using var cts = new CancellationTokenSource();
        var processTask = InScopeAsync(async ctx =>
        {
            var processor = ctx.ServiceProvider.GetRequiredService<
                OutboxMessageProcessor<TestDbContext>
            >();
            await processor.ProcessBatchAsync(includeStuckMessageDetection: false, cts.Token);
        });

        var sendStarted = await blockingSender.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        sendStarted.Should().BeTrue("the sender should have started within 5 seconds");
        await cts.CancelAsync();
        blockingSender.UnblockSend();

        // Assert — should throw OperationCanceledException, not swallow it
        var act = async () => await processTask;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Message should NOT have been marked as failed (no PublishFailed call)
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();
            entity.ErrorCount.Should().Be(0, "cancellation should not count as a send failure");
            entity.IsPoisoned.Should().BeFalse("cancellation should not poison the message");
        });
    }

    [Test]
    public async Task Outbox_DeserializationFailure_PoisonsImmediately()
    {
        // Verifies that a message with corrupt serialized properties is immediately poisoned
        // without consuming any retry budget.
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero));

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
            services.AddSingleton(
                new OutboxOptionsHolder<TestDbContext>(new OutboxOptions { MaxRetries = 5 })
            );
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await InitializeDatabase();

        // Stage a valid message, then corrupt its serialized properties in the database
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "corrupt-msg" });
            await dbContext.SaveChangesAsync();

            var tableName = dbContext
                .Model.FindEntityType(typeof(OutboxMessageEntity))!
                .GetTableName()!;
            await dbContext.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{tableName}\" SET \"SerializedProperties\" = 'invalid-json'"
            );
        });

        // Act
        await InScopeAsync(async ctx =>
        {
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert — must be poisoned immediately, with no retry budget consumed
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();
            entity
                .IsPoisoned.Should()
                .BeTrue("deserialization failure is a terminal configuration error");
            entity
                .ErrorCount.Should()
                .Be(0, "poison on deserialization should not increment the retry counter");
            entity.ProcessedAt.Should().BeNull();
        });
    }

    [Test]
    public async Task Outbox_MissingSender_PoisonsImmediately()
    {
        // Verifies that a message targeting an unregistered transport is immediately poisoned
        // without consuming any retry budget.
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero));

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
            services.AddSingleton(
                new OutboxOptionsHolder<TestDbContext>(new OutboxOptions { MaxRetries = 5 })
            );
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
            // Remove all senders so no sender exists for the staged message's transport
            services.RemoveAll<IMessageSender>();
        });

        await InitializeDatabase();

        // Stage a message (it will have the RabbitMQ transport name, but no sender is registered)
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "no-sender-msg" });
            await dbContext.SaveChangesAsync();
        });

        // Act
        await InScopeAsync(async ctx =>
        {
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
        });

        // Assert — must be poisoned immediately, with no retry budget consumed
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await dbContext.Set<OutboxMessageEntity>().FirstAsync();
            entity.IsPoisoned.Should().BeTrue("missing sender is a terminal configuration error");
            entity
                .ErrorCount.Should()
                .Be(0, "poison on missing sender should not increment the retry counter");
            entity.ProcessedAt.Should().BeNull();
        });
    }

    [Test]
    public async Task Outbox_SaveChangesFailure_DoesNotLoseMessages()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var failOnce = new FailOnceSaveChangesInterceptor();

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
            services.AddSingleton(
                new OutboxOptionsHolder<TestDbContext>(new OutboxOptions { MaxRetries = 5 })
            );
            services.AddSingleton(failOnce);
            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                    options.AddInterceptors(
                        sp.GetRequiredService<FailOnceSaveChangesInterceptor>()
                    );
                }
            );
        });

        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Data = "retry-msg" });

            // First attempt fails (interceptor throws before the DB commit)
            Func<Task> firstAttempt = () => dbContext.SaveChangesAsync();
            await firstAttempt.Should().ThrowAsync<DbUpdateConcurrencyException>();

            // Staged items must survive the failure so the retry can succeed
            dbContext
                .OutboxMessages.Count.Should()
                .Be(1, "staged items must be preserved after SaveChanges failure");

            // Retry succeeds — the interceptor re-processes the staged items
            await dbContext.SaveChangesAsync();
        });

        // Process the outbox and verify the message was persisted
        await InScopeAsync(async ctx =>
        {
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider);
        });

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
            entities
                .Should()
                .HaveCount(1, "exactly one outbox entity should exist (no duplicates from retry)");
        });
    }

    [Test]
    public async Task Outbox_MaxMessageSizeExceeded_SaveChangesThrows()
    {
        const int maxBytes = 80;

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(o => o.WithMaxMessageSize(maxBytes))
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

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var props = new MessageProperties().SetRoutingKey(DefaultRoutingKey);
            props.Transports.Add(RabbitMqConstants.TransportName);
            dbContext.OutboxMessages.Add(
                new TestEvent { Id = "oversized", Data = new string('x', 500) },
                props
            );

            Func<Task> act = () => dbContext.SaveChangesAsync();
            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*exceeds the configured maximum*");
        });

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await dbContext.Set<OutboxMessageEntity>().CountAsync()).Should().Be(0);
        });
    }

    [Test]
    public async Task Outbox_ConcurrentProcessors_ClaimConflictHandled()
    {
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
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

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var props = new MessageProperties().SetRoutingKey(DefaultRoutingKey);
            props.Transports.Add(RabbitMqConstants.TransportName);
            dbContext.OutboxMessages.Add(
                new TestEvent { Id = "concurrent-claim", Data = "x" },
                props
            );
            await dbContext.SaveChangesAsync();
        });

        var beforeCount = await GetMessageCountAsync(QueueName);

        await Task.WhenAll(
            InScopeAsync(async ctx => await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider)),
            InScopeAsync(async ctx => await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider))
        );

        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(QueueName) >= beforeCount + 1,
            TimeSpan.FromSeconds(15)
        );
        (await GetMessageCountAsync(QueueName))
            .Should()
            .Be(
                beforeCount + 1,
                "concurrent claim conflict handling should result in exactly one delivery"
            );

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entities = await dbContext.Set<OutboxMessageEntity>().ToListAsync();
            entities.Should().HaveCount(1);
            entities[0]
                .ProcessedAt.Should()
                .NotBeNull("exactly one worker should complete the send");
            entities[0].ErrorCount.Should().Be(0);
        });
    }

    [Test]
    public async Task Outbox_StagingNonGenericAdd_PersistsAndDelivers()
    {
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(
                    ExchangeName,
                    c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, options) =>
                {
                    options.UseNpgsql(PostgresConnectionString);
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            );
        });

        await EnsureQueueBoundAsync(QueueName, ExchangeName, DefaultRoutingKey);
        await InitializeDatabase();

        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            object message = new TestEvent { Id = "non-generic-add", Data = "via-object" };
            var props = new MessageProperties().SetRoutingKey(DefaultRoutingKey);
            props.Transports.Add(RabbitMqConstants.TransportName);
            dbContext.OutboxMessages.Add(message, props);
            await dbContext.SaveChangesAsync();
        });

        var body = await WaitForMessageAsync(QueueName);
        body.Should().NotBeNull();
        System
            .Text.Encoding.UTF8.GetString(body.Body.ToArray())
            .Should()
            .Contain("non-generic-add");
    }

    /// <summary>
    /// Interceptor that throws on the first SaveChanges attempt, then passes through.
    /// Throws from SavingChangesAsync to simulate a failure before the DB commit.
    /// </summary>
    public class FailOnceSaveChangesInterceptor : SaveChangesInterceptor
    {
        private bool _hasThrown;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (!_hasThrown)
            {
                _hasThrown = true;
                throw new DbUpdateConcurrencyException("Simulated failure", (Exception?)null);
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Sender that succeeds for the first N calls, then always fails.
    /// Used to test per-message save behavior.
    /// </summary>
    private class SucceedThenFailSender(string transportName, int successesBeforeFailure)
        : IMessageSender
    {
        private int _callCount;
        public string TransportName => transportName;

        public Task SendAsync(
            byte[] content,
            MessageProperties props,
            CancellationToken cancellationToken
        )
        {
            _callCount++;
            if (_callCount > successesBeforeFailure)
            {
                throw new InvalidOperationException(
                    $"Simulated failure (attempt {_callCount.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
                );
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Sender that blocks indefinitely until its cancellation token fires.
    /// Used to test send timeout behavior.
    /// </summary>
    private class SlowMessageSender(string transportName) : IMessageSender
    {
        public string TransportName => transportName;

        public async Task SendAsync(
            byte[] content,
            MessageProperties props,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>
    /// Sender that blocks until explicitly unblocked, propagating the cancellation token.
    /// Used to test caller cancellation behavior.
    /// </summary>
    private sealed class BlockingMessageSender(string transportName) : IMessageSender, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(0, 1);
        public string TransportName => transportName;
        public SemaphoreSlim SendStarted { get; } = new(0, 1);

        public void UnblockSend() => _gate.Release();

        public async Task SendAsync(
            byte[] content,
            MessageProperties props,
            CancellationToken cancellationToken
        )
        {
            SendStarted.Release();
            await _gate.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            SendStarted.Dispose();
            _gate.Dispose();
        }
    }
}
