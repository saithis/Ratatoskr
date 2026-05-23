using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.OpenTelemetry;

public class OpenTelemetryTracingTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : OpenTelemetryTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Tracing_WithOutbox_PropagatesContext()
    {
        // 1. Setup ActivityListener
        var activities = new ConcurrentBag<Activity>();
        using var listener = CreateActivityListener(activities);
        ActivitySource.AddActivityListener(listener);

        // 2. Setup Ratatoskr with Outbox
        var handler = new TestEventHandler();
        await StartTestAsync(services => ConfigureRatatoskr(services, handler, useOutbox: true));

        await InitializeDatabase();

        // 3. Act - Publish Message via Outbox
        var eventId = "otel-outbox-1";
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var initialTraceParent =
                $"00-{ActivityTraceId.CreateRandom().ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";
            dbContext.OutboxMessages.Add(
                new TestEvent { Id = eventId, Data = "trace-me" },
                new MessageProperties { Id = eventId, TraceParent = initialTraceParent }
            );
            await dbContext.SaveChangesAsync();
        });

        // 4. Wait for processing
        await WaitForConditionAsync(
            () => handler.HandledMessages.Any(m => m.Id == eventId),
            TimeSpan.FromSeconds(10)
        );

        // 5. Assert Activity Structure
        var relevantActivities = GetRelevantActivities(activities, eventId);

        var outboxActivity = relevantActivities.FirstOrDefault(a =>
            a.OperationName == "create outbox"
        );
        var sendActivity = relevantActivities.FirstOrDefault(a =>
            a.OperationName.StartsWith("send ")
        );
        var processActivity = relevantActivities.FirstOrDefault(a =>
            a.OperationName.StartsWith("process ")
        );

        outboxActivity.Should().NotBeNull("OutboxProcess activity should exist");
        sendActivity.Should().NotBeNull("Send activity should exist");
        processActivity.Should().NotBeNull("Process activity should exist");

        // Verify Hierarchy — RabbitMQ.Client 7.x inserts a transport-level span between
        // send and process, so we verify trace continuity rather than direct parent-child.
        sendActivity!.ParentId.Should().Be(outboxActivity!.Id);
        processActivity!.TraceId.Should().Be(sendActivity.TraceId);

        // Verify Kinds
        outboxActivity.Kind.Should().Be(ActivityKind.Producer);
        sendActivity.Kind.Should().Be(ActivityKind.Client);
        processActivity.Kind.Should().Be(ActivityKind.Consumer);

        // Verify Tags
        AssertActivityTags(
            processActivity,
            ExchangeName,
            QueueName,
            RoutingKey,
            eventId,
            operationName: "process",
            operationType: "process"
        );
        AssertActivityTags(
            sendActivity,
            ExchangeName,
            queueName: null,
            RoutingKey,
            eventId,
            operationName: "send",
            operationType: "send"
        );

        outboxActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.system" && (string?)t.Value == "ratatoskr");
        outboxActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId);
        outboxActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.name" && (string?)t.Value == "create");
        outboxActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.type" && (string?)t.Value == "create");

        // Verify delivery tag on consumer span
        processActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.rabbitmq.message.delivery_tag");
    }

    [Test]
    public async Task Tracing_WithoutOutbox_PropagatesContext()
    {
        // 1. Setup ActivityListener
        var activities = new ConcurrentBag<Activity>();
        using var listener = CreateActivityListener(activities);
        ActivitySource.AddActivityListener(listener);

        // 2. Setup Ratatoskr WITHOUT Outbox
        var handler = new TestEventHandler();
        await StartTestAsync(services => ConfigureRatatoskr(services, handler, useOutbox: false));

        await InitializeDatabase();

        // 3. Act - Publish Message Direct
        var eventId = "otel-direct-1";
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = eventId, Data = "trace-me-direct" },
                new MessageProperties { Id = eventId }
            );
        });

        // 4. Wait for processing
        await WaitForConditionAsync(
            () => handler.HandledMessages.Any(m => m.Id == eventId),
            TimeSpan.FromSeconds(10)
        );

        // 5. Assert Activity Structure
        var relevantActivities = GetRelevantActivities(activities, eventId);

        var publishActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "publish");
        var sendActivity = relevantActivities.FirstOrDefault(a =>
            a.OperationName.StartsWith("send ")
        );
        var processActivity = relevantActivities.FirstOrDefault(a =>
            a.OperationName.StartsWith("process ")
        );
        var outboxActivity = relevantActivities.FirstOrDefault(a =>
            a.OperationName == "create outbox"
        );

        publishActivity.Should().NotBeNull("Publish activity should exist");
        sendActivity.Should().NotBeNull("Send activity should exist");
        processActivity.Should().NotBeNull("Process activity should exist");
        outboxActivity.Should().BeNull("OutboxProcess activity should NOT exist");

        // Verify Hierarchy — RabbitMQ.Client 7.x inserts a transport-level span between
        // send and process, so we verify trace continuity rather than direct parent-child.
        sendActivity!.ParentId.Should().Be(publishActivity!.Id);
        processActivity!.TraceId.Should().Be(sendActivity.TraceId);

        // Verify Kinds
        publishActivity.Kind.Should().Be(ActivityKind.Producer);
        sendActivity.Kind.Should().Be(ActivityKind.Client);
        processActivity.Kind.Should().Be(ActivityKind.Consumer);

        // Verify Tags
        AssertActivityTags(
            processActivity,
            ExchangeName,
            QueueName,
            RoutingKey,
            eventId,
            operationName: "process",
            operationType: "process"
        );
        AssertActivityTags(
            sendActivity,
            ExchangeName,
            queueName: null,
            RoutingKey,
            eventId,
            operationName: "send",
            operationType: "send"
        );

        publishActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.system" && (string?)t.Value == "ratatoskr");
        publishActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId);
        publishActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.name" && (string?)t.Value == "publish");
        publishActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.type" && (string?)t.Value == "create");

        // Verify delivery tag on consumer span
        processActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.rabbitmq.message.delivery_tag");
    }

    [Test]
    public async Task Tracing_Inbox_EmitsDeliverSpan()
    {
        // 1. Setup ActivityListener
        var activities = new ConcurrentBag<Activity>();
        using var listener = CreateActivityListener(activities);
        ActivitySource.AddActivityListener(listener);

        // 2. Setup EF Core transport + inbox
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "otel-inbox-trace",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "otel-inbox-trace",
                    c =>
                        c.Consumes<TestEvent>(m =>
                                m.WithHandler<NoOpTestEventHandler>("otel-trace-noop")
                            )
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(inbox => inbox.WithPollingInterval(TimeSpan.FromMilliseconds(500)))
                );
            });
            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        // 3. Publish with a known message ID
        var eventId = "otel-inbox-trace-1";
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = eventId, Data = "inbox trace" },
                new MessageProperties { Id = eventId }
            );
        });

        // 4. Wait for the "deliver inbox" span to appear
        await WaitForConditionAsync(
            () =>
                activities.Any(a =>
                    a.OperationName == "deliver inbox"
                    && a.TagObjects.Any(t =>
                        t.Key == "messaging.message.id" && (string?)t.Value == eventId
                    )
                ),
            TimeSpan.FromSeconds(15)
        );

        // 5. Assert — span has correct kind, system, and handler key
        var deliverActivity = activities.First(a =>
            a.OperationName == "deliver inbox"
            && a.TagObjects.Any(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId)
        );

        deliverActivity.Kind.Should().Be(ActivityKind.Consumer);
        deliverActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.system" && (string?)t.Value == "ratatoskr");
        deliverActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.name" && (string?)t.Value == "deliver");
        deliverActivity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.type" && (string?)t.Value == "process");
        deliverActivity
            .TagObjects.Should()
            .Contain(t =>
                t.Key == "ratatoskr.inbox.handler.key" && (string?)t.Value == "otel-trace-noop"
            );

        // Verify trace context propagation: the deliver span lives in the same trace as the original publish.
        // Filter by message ID to avoid picking up a publish activity from another parallel test.
        var publishActivity = activities.FirstOrDefault(a =>
            a.OperationName == "publish"
            && a.TagObjects.Any(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId)
        );
        publishActivity.Should().NotBeNull("publish span should exist for the same message");
        deliverActivity
            .TraceId.Should()
            .Be(
                publishActivity.TraceId,
                "inbox delivery must propagate the original message trace ID"
            );

        // Verify no error status on successful delivery
        deliverActivity.Status.Should().Be(ActivityStatusCode.Unset);
    }
}
