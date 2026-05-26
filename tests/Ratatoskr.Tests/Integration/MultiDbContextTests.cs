using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

/// <summary>
/// Integration tests for multi-DbContext inbox/outbox support.
/// Each DbContext gets its own database, inbox processor, and outbox processor.
/// </summary>
public class MultiDbContextTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    /// <summary>
    /// Connection string for the second database, used by SecondTestDbContext.
    /// </summary>
    private string SecondPostgresConnectionString
    {
        get
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            {
                Database = $"test_{TestId}_second",
                MaxPoolSize = 2,
            };
            return builder.ToString();
        }
    }

    public override async Task StartTestAsync(Action<IServiceCollection>? configure = null)
    {
        // Create the second database before the base class starts
        await CreateSecondDatabaseAsync();
        await base.StartTestAsync(configure);
    }

    private async Task CreateSecondDatabaseAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"test_{TestId}_second\"";
        await command.ExecuteNonQueryAsync();
    }

    #region Tests

    [Test]
    public async Task Inbox_TwoDbContexts_EachProcessesOwnHandlers()
    {
        // Arrange: Two channels, each with its own DbContext and inbox handlers.
        // The same message type (TestEvent) is consumed on both channels.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "shared-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );

                bus.AddEventConsumeChannel(
                    "channel-a",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext1>("ctx1-handler")
                            )
                            .UseInbox<TestDbContext>()
                );

                bus.AddEventConsumeChannel(
                    "channel-b",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext2>("ctx2-handler")
                            )
                            .UseInbox<SecondTestDbContext>()
                );

                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing())
                );
                bus.AddEfCoreDurability<SecondTestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
            services.AddDbContext<SecondTestDbContext>(
                (sp, opts) => opts.UseNpgsql(SecondPostgresConnectionString)
            );
        });

        await InitializeBothDatabasesAsync();

        // Act: Publish a message — both channels should accept it via their respective interceptors
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "multi-db-1", Data = "test" },
                new MessageProperties { Id = "multi-db-msg-1" }
            );
        });

        // Wait for inbox entries to appear in both databases
        await WaitForInboxEntriesAsync<TestDbContext>(1);
        await WaitForInboxEntriesAsync<SecondTestDbContext>(1);

        // Process inbox for each DbContext separately
        await InScopeAsync(async ctx =>
            await ProcessInboxAsync<TestDbContext>(ctx.ServiceProvider)
        );
        await InScopeAsync(async ctx =>
            await ProcessInboxAsync<SecondTestDbContext>(ctx.ServiceProvider)
        );

        // Assert: Each database has exactly one handler status, marked as completed
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(1);
            statuses[0].HandlerKey.Should().Be("ctx1-handler");
            statuses[0].CompletedAt.Should().NotBeNull();
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(1);
            statuses[0].HandlerKey.Should().Be("ctx2-handler");
            statuses[0].CompletedAt.Should().NotBeNull();
        });
    }

    [Test]
    public async Task Inbox_TwoDbContexts_ProcessorIsolation_OnlyProcessesOwnDatabase()
    {
        // Arrange: Each DbContext has its own inbox. Processing one should NOT affect the other.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "shared-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );

                bus.AddEventConsumeChannel(
                    "channel-a",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext1>("ctx1-handler")
                            )
                            .UseInbox<TestDbContext>()
                );

                bus.AddEventConsumeChannel(
                    "channel-b",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext2>("ctx2-handler")
                            )
                            .UseInbox<SecondTestDbContext>()
                );

                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing())
                );
                bus.AddEfCoreDurability<SecondTestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
            services.AddDbContext<SecondTestDbContext>(
                (sp, opts) => opts.UseNpgsql(SecondPostgresConnectionString)
            );
        });

        await InitializeBothDatabasesAsync();

        // Publish message
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "isolation-1", Data = "test" },
                new MessageProperties { Id = "isolation-msg-1" }
            );
        });

        await WaitForInboxEntriesAsync<TestDbContext>(1);
        await WaitForInboxEntriesAsync<SecondTestDbContext>(1);

        // Only process the first DbContext's inbox
        await InScopeAsync(async ctx =>
            await ProcessInboxAsync<TestDbContext>(ctx.ServiceProvider)
        );

        // Assert: First DbContext's handler is completed
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.CompletedAt.Should().NotBeNull("first DbContext's handler should be completed");
        });

        // Assert: Second DbContext's handler is still pending (not processed)
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status
                .CompletedAt.Should()
                .BeNull("second DbContext's handler should NOT have been processed");
        });
    }

    [Test]
    public async Task Inbox_TwoDbContexts_SameMessageType_MultipleHandlersPerChannel()
    {
        // Arrange: Both channels consume TestEvent with multiple handlers each
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "shared-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );

                bus.AddEventConsumeChannel(
                    "channel-a",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext1>("ctx1-handler-a")
                                    .WithHandler<HandlerForDbContext1B>("ctx1-handler-b")
                            )
                            .UseInbox<TestDbContext>()
                );

                bus.AddEventConsumeChannel(
                    "channel-b",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext2>("ctx2-handler-a")
                                    .WithHandler<HandlerForDbContext2B>("ctx2-handler-b")
                            )
                            .UseInbox<SecondTestDbContext>()
                );

                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing())
                );
                bus.AddEfCoreDurability<SecondTestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
            services.AddDbContext<SecondTestDbContext>(
                (sp, opts) => opts.UseNpgsql(SecondPostgresConnectionString)
            );
        });

        await InitializeBothDatabasesAsync();

        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "multi-handler-1", Data = "test" },
                new MessageProperties { Id = "multi-handler-msg-1" }
            );
        });

        await WaitForInboxEntriesAsync<TestDbContext>(2);
        await WaitForInboxEntriesAsync<SecondTestDbContext>(2);

        // Process both
        await InScopeAsync(async ctx =>
            await ProcessInboxAsync<TestDbContext>(ctx.ServiceProvider)
        );
        await InScopeAsync(async ctx =>
            await ProcessInboxAsync<SecondTestDbContext>(ctx.ServiceProvider)
        );

        // Assert: Each database has exactly 2 completed handlers
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>()
                .OrderBy(s => s.HandlerKey)
                .ToListAsync();
            statuses.Should().HaveCount(2);
            statuses[0].HandlerKey.Should().Be("ctx1-handler-a");
            statuses[1].HandlerKey.Should().Be("ctx1-handler-b");
            statuses.Should().AllSatisfy(s => s.CompletedAt.Should().NotBeNull());
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>()
                .OrderBy(s => s.HandlerKey)
                .ToListAsync();
            statuses.Should().HaveCount(2);
            statuses[0].HandlerKey.Should().Be("ctx2-handler-a");
            statuses[1].HandlerKey.Should().Be("ctx2-handler-b");
            statuses.Should().AllSatisfy(s => s.CompletedAt.Should().NotBeNull());
        });
    }

    [Test]
    public async Task Outbox_TwoDbContexts_EachProcessesOwnOutbox()
    {
        // Arrange: Two DbContexts with outbox, each stages messages independently.
        // We manually register outbox components without background services for deterministic control.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "outbox-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
            });

            // Manually register outbox components for both DbContexts (without hosted services)
            services.AddSingleton<IMessageSender, EfCoreMessageSender>();

            services.AddSingleton(
                new OutboxOptionsHolder<TestDbContext>(
                    new OutboxOptions { LockName = $"OutboxProcessor_{nameof(TestDbContext)}" }
                )
            );
            services.AddSingleton<OutboxTriggerInterceptor<TestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<TestDbContext>>();
            services.AddSingleton<OutboxProcessor<TestDbContext>>();

            services.AddSingleton(
                new OutboxOptionsHolder<SecondTestDbContext>(
                    new OutboxOptions
                    {
                        LockName = $"OutboxProcessor_{nameof(SecondTestDbContext)}",
                    }
                )
            );
            services.AddSingleton<OutboxTriggerInterceptor<SecondTestDbContext>>();
            services.AddTransient<OutboxMessageProcessor<SecondTestDbContext>>();
            services.AddSingleton<OutboxProcessor<SecondTestDbContext>>();

            services.AddDbContext<TestDbContext>(
                (sp, opts) =>
                {
                    opts.UseNpgsql(PostgresConnectionString);
                    opts.RegisterOutbox<TestDbContext>(sp);
                }
            );
            services.AddDbContext<SecondTestDbContext>(
                (sp, opts) =>
                {
                    opts.UseNpgsql(SecondPostgresConnectionString);
                    opts.RegisterOutbox<SecondTestDbContext>(sp);
                }
            );
        });

        await InitializeBothDatabasesAsync();

        // Stage messages in each DbContext
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.OutboxMessages.Add(new TestEvent { Id = "ctx1-outbox-1", Data = "from-ctx1" });
            await db.SaveChangesAsync();
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            db.OutboxMessages.Add(new TestEvent { Id = "ctx2-outbox-1", Data = "from-ctx2" });
            await db.SaveChangesAsync();
        });

        // Process only the first DbContext's outbox
        await InScopeAsync(async ctx =>
            await ProcessOutboxAsync<TestDbContext>(ctx.ServiceProvider)
        );

        // Assert: First DbContext's message is processed
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await db.Set<OutboxMessageEntity>().SingleAsync();
            entity
                .ProcessedAt.Should()
                .NotBeNull("first DbContext's outbox message should be processed");
        });

        // Assert: Second DbContext's message is still pending
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var entity = await db.Set<OutboxMessageEntity>().SingleAsync();
            entity
                .ProcessedAt.Should()
                .BeNull("second DbContext's outbox message should NOT be processed yet");
        });

        // Now process the second DbContext's outbox
        await InScopeAsync(async ctx =>
            await ProcessOutboxAsync<SecondTestDbContext>(ctx.ServiceProvider)
        );

        // Assert: Second DbContext's message is now processed too
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var entity = await db.Set<OutboxMessageEntity>().SingleAsync();
            entity
                .ProcessedAt.Should()
                .NotBeNull("second DbContext's outbox message should now be processed");
        });
    }

    [Test]
    public async Task Inbox_TwoDbContexts_FullEndToEnd_OutboxToInbox()
    {
        // Arrange: Full pipeline with two DbContexts — outbox → EF Core transport → inbox
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "shared-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );

                bus.AddEventConsumeChannel(
                    "channel-a",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext1>("ctx1-handler")
                            )
                            .UseInbox<TestDbContext>()
                );

                bus.AddEventConsumeChannel(
                    "channel-b",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext2>("ctx2-handler")
                            )
                            .UseInbox<SecondTestDbContext>()
                );

                bus.AddEfCoreDurability<TestDbContext>(d =>
                {
                    d.UseInbox(i => i.WithoutBackgroundProcessing());
                    d.UseOutbox();
                });
                bus.AddEfCoreDurability<SecondTestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) =>
                {
                    opts.UseNpgsql(PostgresConnectionString);
                    opts.RegisterOutbox<TestDbContext>(sp);
                }
            );
            services.AddDbContext<SecondTestDbContext>(
                (sp, opts) => opts.UseNpgsql(SecondPostgresConnectionString)
            );
        });

        await InitializeBothDatabasesAsync();

        // Stage message via outbox on TestDbContext
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            db.TestEntities.Add(new TestEntity { Name = "Business Data" });
            db.OutboxMessages.Add(new TestEvent { Id = "e2e-multi-1", Data = "outbox-to-inbox" });
            await db.SaveChangesAsync();
        });

        // Wait for outbox to deliver and inbox entries to appear
        await WaitForInboxEntriesAsync<TestDbContext>(1, TimeSpan.FromSeconds(15));
        await WaitForInboxEntriesAsync<SecondTestDbContext>(1, TimeSpan.FromSeconds(15));

        // Process both inboxes
        await InScopeAsync(async ctx =>
            await ProcessInboxAsync<TestDbContext>(ctx.ServiceProvider)
        );
        await InScopeAsync(async ctx =>
            await ProcessInboxAsync<SecondTestDbContext>(ctx.ServiceProvider)
        );

        // Assert: Both handlers completed
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be("ctx1-handler");
            status.CompletedAt.Should().NotBeNull();
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be("ctx2-handler");
            status.CompletedAt.Should().NotBeNull();
        });
    }

    [Test]
    public async Task Inbox_TwoDbContexts_ConcurrentProcessing_NoInterference()
    {
        // Arrange: Both DbContexts process their inboxes concurrently — should not interfere.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "shared-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );

                bus.AddEventConsumeChannel(
                    "channel-a",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext1>("ctx1-handler")
                            )
                            .UseInbox<TestDbContext>()
                );

                bus.AddEventConsumeChannel(
                    "channel-b",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<HandlerForDbContext2>("ctx2-handler")
                            )
                            .UseInbox<SecondTestDbContext>()
                );

                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing())
                );
                bus.AddEfCoreDurability<SecondTestDbContext>(d =>
                    d.UseInbox(i => i.WithoutBackgroundProcessing())
                );
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
            services.AddDbContext<SecondTestDbContext>(
                (sp, opts) => opts.UseNpgsql(SecondPostgresConnectionString)
            );
        });

        await InitializeBothDatabasesAsync();

        // Publish multiple messages
        for (var i = 0; i < 5; i++)
        {
            var index = i;
            await InScopeAsync(async ctx =>
            {
                var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(
                    new TestEvent
                    {
                        Id =
                            $"concurrent-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                        Data =
                            $"data-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    },
                    new MessageProperties
                    {
                        Id =
                            $"concurrent-msg-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    }
                );
            });
        }

        await WaitForInboxEntriesAsync<TestDbContext>(5, TimeSpan.FromSeconds(15));
        await WaitForInboxEntriesAsync<SecondTestDbContext>(5, TimeSpan.FromSeconds(15));

        // Act: Process both inboxes concurrently
        var task1 = Task.Run(async () =>
        {
            await InScopeAsync(async ctx =>
                await ProcessInboxAsync<TestDbContext>(ctx.ServiceProvider)
            );
        });
        var task2 = Task.Run(async () =>
        {
            await InScopeAsync(async ctx =>
                await ProcessInboxAsync<SecondTestDbContext>(ctx.ServiceProvider)
            );
        });

        await Task.WhenAll(task1, task2);

        // Assert: Both databases have all 5 handler statuses completed
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(5);
            statuses.Should().AllSatisfy(s => s.CompletedAt.Should().NotBeNull());
        });

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(5);
            statuses.Should().AllSatisfy(s => s.CompletedAt.Should().NotBeNull());
        });
    }

    #endregion

    #region Helpers

    private async Task InitializeBothDatabasesAsync()
    {
        await InScopeAsync(async ctx =>
        {
            var db1 = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await db1.Database.EnsureCreatedAsync();

            var db2 = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            await db2.Database.EnsureCreatedAsync();
        });
    }

    private async Task WaitForInboxEntriesAsync<TDbContext>(
        int expectedCount,
        TimeSpan? timeout = null
    )
        where TDbContext : DbContext, IInboxDbContext
    {
        await WaitForConditionAsync(
            async () =>
                await InScopeAsync(async ctx =>
                {
                    var db = ctx.ServiceProvider.GetRequiredService<TDbContext>();
                    var count = await db.Set<InboxHandlerStatusEntity>().CountAsync();
                    return count >= expectedCount;
                }),
            timeout ?? TimeSpan.FromSeconds(10),
            $"Expected {expectedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} inbox handler status entries in {typeof(TDbContext).Name} within timeout"
        );
    }

    private static async Task<int> ProcessInboxAsync<TDbContext>(IServiceProvider serviceProvider)
        where TDbContext : DbContext, IInboxDbContext
    {
        var total = 0;
        while (true)
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<
                InboxMessageProcessor<TDbContext>
            >();
            var count = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                CancellationToken.None
            );
            total += count;
            if (count == 0)
            {
                break;
            }
        }
        return total;
    }

    private static async Task<int> ProcessOutboxAsync<TDbContext>(IServiceProvider serviceProvider)
        where TDbContext : DbContext, IOutboxDbContext
    {
        var total = 0;
        while (true)
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<
                OutboxMessageProcessor<TDbContext>
            >();
            var count = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                CancellationToken.None
            );
            total += count;
            if (count == 0)
            {
                break;
            }
        }
        return total;
    }

    #endregion

    #region Test Handlers

    private class HandlerForDbContext1 : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(
            TestEvent message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private class HandlerForDbContext1B : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(
            TestEvent message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private class HandlerForDbContext2 : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(
            TestEvent message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private class HandlerForDbContext2B : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(
            TestEvent message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    #endregion
}
