using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

public class MessageTrackingTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string QueueName => $"track-queue-{TestId}";
    private string ExchangeName => $"track-exchange-{TestId}";
    private string DefaultRoutingKey => "test.event";

    [Test]
    public async Task Tracking_PublishDirect_CapturesPublishedAndSent()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
            services.AddRatatoskrTesting();
        });

        var queueName = $"track-pub-{TestId}";
        await EnsureQueueBoundAsync(queueName, ExchangeName, DefaultRoutingKey);

        await using var session = Services.CreateTrackingSession();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            var props = new MessageProperties();
            props.SetRoutingKey(DefaultRoutingKey);
            await bus.PublishDirectAsync(new TestEvent { Id = "track-pub-1", Data = "tracked publish" }, props);
        });

        // Assert - Published stage
        var published = await session.WaitForPublished<TestEvent>(TimeSpan.FromSeconds(5));
        published.GetMessage<TestEvent>().Id.Should().Be("track-pub-1");
        published.Properties.Type.Should().Be("test.event");
        published.RawBody.Should().NotBeNull();

        // Assert - Sent stage (on the wire)
        var sent = await session.WaitForSent<TestEvent>(TimeSpan.FromSeconds(5));
        sent.RawBody.Should().NotBeNull();
        Encoding.UTF8.GetString(sent.RawBody!).Should().Contain("track-pub-1");
    }

    [Test]
    public async Task Tracking_ConsumeMessage_CapturesReceivedAndDispatched()
    {
        // Arrange
        var handler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                ConfigureConsumeBus(bus);
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        // Act - publish through the library pipeline (gets trace context)
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "track-cons-1", Data = "tracked consume" });
        });

        // Assert - Dispatched stage
        var dispatched = await session.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched.GetMessage<TestEvent>().Id.Should().Be("track-cons-1");
        dispatched.Result.Should().Be(DispatchResult.Success);

        // Assert - Received stage
        session.Received.ShouldHaveMessage<TestEvent>();
    }

    [Test]
    public async Task Tracking_EndToEnd_CapturesAllStages()
    {
        // Arrange
        var handler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                ConfigureConsumeBus(bus);
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "e2e-1", Data = "end to end" });
        });

        // Wait for the full pipeline
        var dispatched = await session.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched.GetMessage<TestEvent>().Id.Should().Be("e2e-1");

        // Assert all stages are captured
        session.Published.Count.Should().BeGreaterThanOrEqualTo(1);
        session.Sent.Count.Should().BeGreaterThanOrEqualTo(1);
        session.Received.Count.Should().BeGreaterThanOrEqualTo(1);
        session.Dispatched.Count.Should().BeGreaterThanOrEqualTo(1);

        // Published should also reference the same message
        session.Published.Single<TestEvent>().GetMessage<TestEvent>().Id.Should().Be("e2e-1");
    }

    [Test]
    public async Task Tracking_OutboxMessage_CapturesOutboxStages()
    {
        // Arrange
        var handler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o.WithQueueName(QueueName).WithAutoAck(false).WithTransientQueue()
                        .WithQueueType(QueueType.Classic))
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
                bus.AddEfCoreOutbox<TestDbContext>();
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.RegisterOutbox<TestDbContext>(sp);
            });
            services.AddRatatoskrTesting();
        });

        await InitializeDatabase();

        await using var session = Services.CreateTrackingSession();

        // Act - stage message via outbox
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.OutboxMessages.Add(new TestEvent { Id = "outbox-track-1", Data = "outbox tracked" },
                new MessageProperties().SetExchange(QueueName));
            await dbContext.SaveChangesAsync();
        });

        // Wait for the full outbox pipeline to complete
        var dispatched = await session.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(10));
        dispatched.Result.Should().Be(DispatchResult.Success);

        // Assert - OutboxStaged (synchronous during SaveChanges, always available)
        var staged = session.OutboxStaged.Single<TestEvent>();
        staged.GetMessage<TestEvent>().Id.Should().Be("outbox-track-1");
    }

    [Test]
    public async Task Tracking_ParallelTests_IsolatedByTraceId()
    {
        // Arrange - one bus with handler
        var handler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                ConfigureConsumeBus(bus);
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
            });
            services.AddRatatoskrTesting();
        });

        // Act - two sessions publishing different messages
        await using var session1 = Services.CreateTrackingSession();
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "parallel-1", Data = "session 1" });
        });
        var dispatched1 = await session1.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));

        await using var session2 = Services.CreateTrackingSession();
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "parallel-2", Data = "session 2" });
        });
        var dispatched2 = await session2.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));

        // Assert - each session only sees its own messages
        dispatched1.GetMessage<TestEvent>().Id.Should().Be("parallel-1");
        dispatched2.GetMessage<TestEvent>().Id.Should().Be("parallel-2");

        // Sessions have different trace IDs
        session1.TraceId.Should().NotBe(session2.TraceId);
    }

    [Test]
    public async Task Tracking_WaitForDispatched_CapturesFailure()
    {
        // Arrange
        var handler = new ThrowingTestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandPublishChannel(QueueName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddCommandConsumeChannel(QueueName, c => c
                    .WithRabbitMq(o => o
                        .WithQueueName(QueueName)
                        .WithAutoAck(false)
                        .WithRetry(r => r.WithMaxRetries(1).WithDelay(TimeSpan.FromMilliseconds(50)))
                        .WithTransientQueue()
                        .WithQueueType(QueueType.Classic))
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, ThrowingTestEventHandler>(handler);
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "fail-1", Data = "will fail" });
        });

        // Assert - wait for a dispatch (which will be a failure)
        var dispatched = await session.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched.Result.Should().Be(DispatchResult.RecoverableError);
        dispatched.Exception.Should().NotBeNull();
    }

    [Test]
    public async Task Tracking_TransportShape_RawBodyAssertable()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
            services.AddRatatoskrTesting();
        });

        var queueName = $"track-shape-{TestId}";
        await EnsureQueueBoundAsync(queueName, ExchangeName, DefaultRoutingKey);

        await using var session = Services.CreateTrackingSession();

        // Act
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            var props = new MessageProperties();
            props.SetRoutingKey(DefaultRoutingKey);
            await bus.PublishDirectAsync(new TestEvent { Id = "shape-1", Data = "transport shape" }, props);
        });

        // Assert - verify the raw bytes on the wire
        var sent = await session.WaitForSent<TestEvent>(TimeSpan.FromSeconds(5));
        var rawJson = Encoding.UTF8.GetString(sent.RawBody!);
        rawJson.Should().Contain("shape-1");
        rawJson.Should().Contain("transport shape");

        // Verify properties reflect CloudEvents metadata
        sent.Properties.Type.Should().Be("test.event");
        sent.Properties.Id.Should().NotBeNullOrEmpty();
        sent.Properties.Source.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Tracking_ActionBasedApi_WorksEndToEnd()
    {
        // Arrange
        var handler = new TestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                ConfigureConsumeBus(bus);
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
            });
            services.AddRatatoskrTesting();
        });

        // Act - use the action-based API
        await using var session = await Services
            .TrackActivity()
            .Timeout(TimeSpan.FromSeconds(10))
            .WaitForMessage<TestEvent>(MessageStage.Dispatched)
            .ExecuteAndWaitAsync(async () =>
            {
                using var scope = Services.CreateScope();
                var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
                await bus.PublishDirectAsync(new TestEvent { Id = "action-1", Data = "action based" });
            });

        // Assert - the wait already completed, so Dispatched is guaranteed populated
        var dispatched = session.Dispatched.Single<TestEvent>();
        dispatched.GetMessage<TestEvent>().Id.Should().Be("action-1");
        dispatched.Result.Should().Be(DispatchResult.Success);
    }

    [Test]
    public async Task MessageCollection_ShouldHaveNoMessage_ThrowsWhenPresent()
    {
        // Arrange
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(ExchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        // Initially no messages
        session.Published.ShouldHaveNoMessage<TestEvent>();

        // Publish
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            var props = new MessageProperties();
            props.SetRoutingKey(DefaultRoutingKey);
            await bus.PublishDirectAsync(new TestEvent { Id = "neg-1", Data = "negative test" }, props);
        });

        var published = await session.WaitForPublished<TestEvent>(TimeSpan.FromSeconds(5));
        published.GetMessage<TestEvent>().Id.Should().Be("neg-1");

        // Now it should throw
        var act = () => session.Published.ShouldHaveNoMessage<TestEvent>();
        act.Should().Throw<InvalidOperationException>();
    }

    // --- Test Helpers ---

    private void ConfigureConsumeBus(RatatoskrBuilder bus)
    {
        bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
        bus.AddCommandPublishChannel(QueueName, c => c
            .WithRabbitMq(r => r.WithTopicExchange())
            .Produces<TestEvent>());
        bus.AddCommandConsumeChannel(QueueName, c => c
            .WithRabbitMq(o => o.WithQueueName(QueueName).WithAutoAck(false).WithTransientQueue()
                .WithQueueType(QueueType.Classic))
            .Consumes<TestEvent>());
    }

    private async Task InitializeDatabase()
    {
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await dbContext.Database.EnsureCreatedAsync();
        });
    }
}
