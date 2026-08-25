using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.Tests.Integration.Inbox;

namespace Ratatoskr.Tests.Integration;

[ClassDataSource<RabbitMqContainerFixture, PostgresContainerFixture>(
    Shared = [SharedType.PerTestSession, SharedType.PerTestSession]
)]
public class EfCoreTransportTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : InboxTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task PublishDirectAsync_BasicFlow_MessageDeliveredViaInbox()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "efcore-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "efcore-events",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "direct-1", Data = "hello" },
                new MessageProperties { Id = "msg-direct-1" }
            );
        });

        // Assert — inbox entry should be created
        await WaitForInboxEntriesAsync(1);

        // Process inbox and verify handler ran
        await WaitForConditionAsync(
            async () =>
                await InScopeAsync(async ctx =>
                {
                    var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
                    var status = await db.Set<InboxHandlerStatusEntity>()
                        .SingleOrDefaultAsync(s => s.HandlerKey == "handler-a");
                    return status?.CompletedAt != null;
                }),
            TimeSpan.FromSeconds(15)
        );

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var message = await db.Set<InboxMessageEntity>().SingleAsync();
            message.TransportName.Should().Be(EfCoreTransportConstants.TransportName);

            var status = await db.Set<InboxHandlerStatusEntity>().SingleAsync();
            status.HandlerKey.Should().Be("handler-a");
            status.CompletedAt.Should().NotBeNull();
            status.ErrorCount.Should().Be(0);
        });
    }

    [Test]
    public async Task PublishDirectAsync_MultipleConsumeChannels_AllGetInboxEntries()
    {
        // Arrange — two consume channels for the same message type
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
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEventConsumeChannel(
                    "channel-b",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerB>("handler-b"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "multi-1", Data = "multi" },
                new MessageProperties { Id = "msg-multi-1" }
            );
        });

        // Assert — both channels should have inbox entries
        await WaitForInboxEntriesAsync(2);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var statuses = await db.Set<InboxHandlerStatusEntity>().ToListAsync();
            statuses.Should().HaveCount(2);
            statuses.Select(s => s.HandlerKey).Should().BeEquivalentTo(["handler-a", "handler-b"]);
        });
    }

    [Test]
    public async Task PublishDirectAsync_TrackingStages_PublishedAndSentFire()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "track-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "track-events",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
            services.AddRatatoskrTesting();
        });

        await InitializeDatabase();

        // Act
        await using var session = Services.CreateTrackingSession();
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "track-1", Data = "tracking" },
                new MessageProperties { Id = "msg-track-1" }
            );
        });

        // Assert — Published and Sent stages should fire
        var published = await session.WaitForPublishedAsync<TestEvent>(TimeSpan.FromSeconds(5));
        published.Properties.Id.Should().Be("msg-track-1");

        var sent = await session.WaitForSentAsync<TestEvent>(TimeSpan.FromSeconds(5));
        sent.TransportName.Should().Be(EfCoreTransportConstants.TransportName);

        // InboxQueued should also fire
        var queued = await session.WaitForInboxQueuedAsync<TestEvent>(TimeSpan.FromSeconds(5));
        queued.TransportName.Should().Be(EfCoreTransportConstants.TransportName);
    }

    [Test]
    public void PublishDirectAsync_ChannelWithoutInbox_ThrowsAtStartup()
    {
        // Act & Assert — startup validation should catch missing inbox
        var act = () =>
            new ServiceCollection().AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel("events", c => c.WithEfCore().Produces<TestEvent>());
                bus.AddEventConsumeChannel(
                    "events",
                    c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

        act.Should().Throw<InvalidOperationException>().WithMessage("*EF Core transport*UseInbox*");
    }

    [Test]
    public async Task PublishDirectAsync_NoConsumeChannelForType_ThrowsInsteadOfSilentlyDropping()
    {
        // A message routed to the EF Core transport is delivered in-process by writing to the
        // inbox of each matching consume channel. If no consume channel is registered for the
        // type, EfCoreMessageSender used to complete successfully (and the outbox would mark the
        // row processed) even though the message reached no inbox -- a silent message drop. It
        // must now fail loudly instead.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "efcore-orphan",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                // A valid consume channel exists, but for a DIFFERENT message type, so the
                // published TestEvent resolves to zero consume channels.
                bus.AddEventConsumeChannel(
                    "unrelated-events",
                    c =>
                        c.Consumes<OrderCreatedEvent>(m =>
                                m.WithHandler<OrderCreatedInboxHandler>("order-handler")
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
            Func<Task> act = () =>
                bus.PublishDirectAsync(
                    new TestEvent { Id = "orphan-1", Data = "nowhere" },
                    new MessageProperties { Id = "msg-orphan-1" }
                );

            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*no consume channel*");
        });

        // Nothing should have been written to any inbox.
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.Set<InboxMessageEntity>().CountAsync()).Should().Be(0);
        });
    }

    private sealed class OrderCreatedInboxHandler : IMessageHandler<OrderCreatedEvent>
    {
        public Task HandleAsync(
            OrderCreatedEvent message,
            MessageProperties properties,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    [Test]
    public async Task EfCoreTransport_SendActivity_Created()
    {
        // Arrange
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Ratatoskr",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "otel-efcore",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "otel-efcore",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "otel-1", Data = "tracing" },
                new MessageProperties { Id = "msg-otel-1" }
            );
        });

        // Assert — should have a "send efcore" activity for this specific message
        await WaitForConditionAsync(
            () =>
                Task.FromResult(
                    activities.Any(a =>
                        a.OperationName == "send efcore"
                        && a.TagObjects.Any(t =>
                            t.Key == "messaging.message.id" && (string?)t.Value == "msg-otel-1"
                        )
                    )
                ),
            TimeSpan.FromSeconds(5)
        );

        var sendActivity = activities.First(a =>
            a.OperationName == "send efcore"
            && a.TagObjects.Any(t =>
                t.Key == "messaging.message.id" && (string?)t.Value == "msg-otel-1"
            )
        );
        sendActivity.Kind.Should().Be(ActivityKind.Client);
        sendActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.system" && (string?)t.Value == "efcore");
        sendActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.name" && (string?)t.Value == "send");
        sendActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.type" && (string?)t.Value == "send");
        sendActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == "msg-otel-1");
        sendActivity.TagObjects.Should().Contain(t => t.Key == "messaging.message.body.size");
    }

    [Test]
    public async Task EfCoreTransport_Metrics_Recorded()
    {
        // Arrange
        var metricMeasurements =
            new ConcurrentBag<(
                string InstrumentName,
                double Value,
                KeyValuePair<string, object?>[] Tags
            )>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Ratatoskr")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, _) =>
                metricMeasurements.Add((instrument.Name, measurement, tags.ToArray()))
        );
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
                metricMeasurements.Add((instrument.Name, measurement, tags.ToArray()))
        );
        meterListener.Start();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "metrics-efcore",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "metrics-efcore",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<InboxHandlerA>("handler-a"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            });

            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "metric-1", Data = "metrics" },
                new MessageProperties { Id = "msg-metric-1" }
            );
        });

        // Assert — should have sent message count and duration metrics
        await WaitForConditionAsync(
            () =>
                Task.FromResult(
                    metricMeasurements.Any(m =>
                        m.InstrumentName == "messaging.client.sent.messages"
                        && m.Tags.Any(t =>
                            t.Key == "messaging.system" && (string?)t.Value == "efcore"
                        )
                    )
                ),
            TimeSpan.FromSeconds(5)
        );

        var sentMetric = metricMeasurements.First(m =>
            m.InstrumentName == "messaging.client.sent.messages"
            && m.Tags.Any(t => t.Key == "messaging.system" && (string?)t.Value == "efcore")
        );
        sentMetric.Value.Should().Be(1);

        var durationMetric = metricMeasurements.First(m =>
            m.InstrumentName == "messaging.client.operation.duration"
            && m.Tags.Any(t => t.Key == "messaging.system" && (string?)t.Value == "efcore")
        );
        durationMetric.Value.Should().BeGreaterThan(0);
    }
}
