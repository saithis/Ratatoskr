using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.Local;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

public class OpenTelemetryTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string ExchangeName => $"otel-test-{TestId}";
    private string QueueName => $"otel-queue-{TestId}";
    private const string RoutingKey = "test.event";

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
            var initialTraceParent = $"00-{ActivityTraceId.CreateRandom().ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";
            dbContext.OutboxMessages.Add(new TestEvent { Id = eventId, Data = "trace-me" },
                new MessageProperties { Id = eventId, TraceParent = initialTraceParent });
            await dbContext.SaveChangesAsync();
        });

        // 4. Wait for processing
        await WaitForConditionAsync(() => handler.HandledMessages.Any(m => m.Id == eventId), TimeSpan.FromSeconds(10));

        // 5. Assert Activity Structure
        var relevantActivities = GetRelevantActivities(activities, eventId);

        var outboxActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "create outbox");
        var sendActivity = relevantActivities.FirstOrDefault(a => a.OperationName.StartsWith("send "));
        var processActivity = relevantActivities.FirstOrDefault(a => a.OperationName.StartsWith("process "));

        outboxActivity.Should().NotBeNull("OutboxProcess activity should exist");
        sendActivity.Should().NotBeNull("Send activity should exist");
        processActivity.Should().NotBeNull("Process activity should exist");

        // Verify Hierarchy
        sendActivity!.ParentId.Should().Be(outboxActivity!.Id);
        processActivity!.ParentId.Should().Be(sendActivity.Id);

        // Verify Kinds
        outboxActivity.Kind.Should().Be(ActivityKind.Producer);
        sendActivity.Kind.Should().Be(ActivityKind.Client);
        processActivity.Kind.Should().Be(ActivityKind.Consumer);

        // Verify Tags
        AssertActivityTags(processActivity, ExchangeName, QueueName, RoutingKey, eventId,
            operationName: "process", operationType: "process");
        AssertActivityTags(sendActivity, ExchangeName, queueName: null, RoutingKey, eventId,
            operationName: "send", operationType: "send");

        outboxActivity.TagObjects.Should().Contain(t => t.Key == "messaging.system" && (string?)t.Value == "ratatoskr");
        outboxActivity.TagObjects.Should().Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId);
        outboxActivity.TagObjects.Should().Contain(t => t.Key == "messaging.operation.name" && (string?)t.Value == "create");
        outboxActivity.TagObjects.Should().Contain(t => t.Key == "messaging.operation.type" && (string?)t.Value == "create");

        // Verify delivery tag on consumer span
        processActivity.TagObjects.Should().Contain(t => t.Key == "messaging.rabbitmq.message.delivery_tag");
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
            await bus.PublishDirectAsync(new TestEvent { Id = eventId, Data = "trace-me-direct" }, new MessageProperties { Id = eventId });
        });

        // 4. Wait for processing
        await WaitForConditionAsync(() => handler.HandledMessages.Any(m => m.Id == eventId), TimeSpan.FromSeconds(10));

        // 5. Assert Activity Structure
        var relevantActivities = GetRelevantActivities(activities, eventId);

        var publishActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "publish");
        var sendActivity = relevantActivities.FirstOrDefault(a => a.OperationName.StartsWith("send "));
        var processActivity = relevantActivities.FirstOrDefault(a => a.OperationName.StartsWith("process "));
        var outboxActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "create outbox");

        publishActivity.Should().NotBeNull("Publish activity should exist");
        sendActivity.Should().NotBeNull("Send activity should exist");
        processActivity.Should().NotBeNull("Process activity should exist");
        outboxActivity.Should().BeNull("OutboxProcess activity should NOT exist");

        // Verify Hierarchy
        sendActivity!.ParentId.Should().Be(publishActivity!.Id);
        processActivity!.ParentId.Should().Be(sendActivity.Id);

        // Verify Kinds
        publishActivity.Kind.Should().Be(ActivityKind.Producer);
        sendActivity.Kind.Should().Be(ActivityKind.Client);
        processActivity.Kind.Should().Be(ActivityKind.Consumer);

        // Verify Tags
        AssertActivityTags(processActivity, ExchangeName, QueueName, RoutingKey, eventId,
            operationName: "process", operationType: "process");
        AssertActivityTags(sendActivity, ExchangeName, queueName: null, RoutingKey, eventId,
            operationName: "send", operationType: "send");

        publishActivity.TagObjects.Should().Contain(t => t.Key == "messaging.system" && (string?)t.Value == "ratatoskr");
        publishActivity.TagObjects.Should().Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId);
        publishActivity.TagObjects.Should().Contain(t => t.Key == "messaging.operation.name" && (string?)t.Value == "publish");
        publishActivity.TagObjects.Should().Contain(t => t.Key == "messaging.operation.type" && (string?)t.Value == "create");

        // Verify delivery tag on consumer span
        processActivity.TagObjects.Should().Contain(t => t.Key == "messaging.rabbitmq.message.delivery_tag");
    }

    [Test]
    public async Task Metrics_Are_Recorded_Correctly()
    {
        // 1. Setup MeterListener
        var metricMeasurements = new ConcurrentBag<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var meterListener = CreateMeterListener(metricMeasurements);

        // 2. Setup Ratatoskr with Outbox
        var handler = new TestEventHandler();
        await StartTestAsync(services => ConfigureRatatoskr(services, handler, useOutbox: true));

        await InitializeDatabase();

        // 3. Act - Publish Message
        var eventId = "otel-metrics-1";
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var initialTraceParent = $"00-{ActivityTraceId.CreateRandom().ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";
            dbContext.OutboxMessages.Add(new TestEvent { Id = eventId, Data = "trace-me-metrics" },
                new MessageProperties { Id = eventId, TraceParent = initialTraceParent, Time = DateTimeOffset.UtcNow.AddSeconds(-1) });
            await dbContext.SaveChangesAsync();
        });

        // 4. Wait for processing
        await WaitForConditionAsync(() => handler.HandledMessages.Any(m => m.Id == eventId), TimeSpan.FromSeconds(10));

        // 5. Assert Metrics
        var testMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);

        // Messaging Metrics (standard OTEL names)
        testMetrics.Should().Contain(m => m.InstrumentName == "messaging.client.sent.messages" && m.Value == 1);
        testMetrics.Should().Contain(m => m.InstrumentName == "messaging.client.operation.duration");
        testMetrics.Should().Contain(m => m.InstrumentName == "messaging.client.consumed.messages" && m.Value == 1);
        testMetrics.Should().Contain(m => m.InstrumentName == "messaging.process.duration");

        // Latency Metrics (custom)
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.receive.lag");
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.process.lag");

        // Outbox Metrics
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.outbox.batch.size" && m.Value >= 1);
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.outbox.process.count" && m.Value >= 1);
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.outbox.process.duration");

        // Verify no error.type on successful processing
        var processDuration = testMetrics.First(m => m.InstrumentName == "messaging.process.duration");
        processDuration.Tags.Should().NotContain(t => t.Key == "error.type");

        // Verify Retry/DLQ metrics don't exist (no failures)
        testMetrics.Any(m => m.InstrumentName == "ratatoskr.retry.messages").Should().BeFalse();
        testMetrics.Any(m => m.InstrumentName == "ratatoskr.dead_letter.messages").Should().BeFalse();

        // Verify Tags on send metrics
        var sendMetric = testMetrics.First(m => m.InstrumentName == "messaging.client.sent.messages");
        AssertMetricTags(sendMetric, ExchangeName, queueName: null, RoutingKey);

        // Verify Tags on consume metrics
        var consumeMetric = testMetrics.First(m => m.InstrumentName == "messaging.client.consumed.messages");
        AssertMetricTags(consumeMetric, ExchangeName, QueueName, RoutingKey);

        // Verify duration is in seconds (should be small values, not hundreds)
        var durationMetric = testMetrics.First(m => m.InstrumentName == "messaging.client.operation.duration");
        durationMetric.Value.Should().BeLessThan(30, "duration should be in seconds, not milliseconds");
    }

    [Test]
    public async Task Telemetry_Record_Failures_And_Retries()
    {
        // 1. Setup Listeners
        var metricMeasurements = new ConcurrentBag<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var meterListener = CreateMeterListener(metricMeasurements);

        var activities = new ConcurrentBag<Activity>();
        using var activityListener = CreateActivityListener(activities);
        ActivitySource.AddActivityListener(activityListener);

        // 2. Setup Ratatoskr with a Failing Handler (fails 2 times)
        var handler = new FailingTestEventHandler(failuresBeforeSuccess: 2);

        await StartTestAsync(services => ConfigureRatatoskr(services, handler, useOutbox: false,
            configureConsumer: c => c.WithRetry(3, TimeSpan.FromMilliseconds(100))));

        // 3. Act - Publish Message
        var eventId = "otel-retry-1";
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = eventId, Data = "retry-me" }, new MessageProperties { Id = eventId });
        });

        // 4. Wait for processing (Success)
        await WaitForConditionAsync(() => handler.HandledMessages.Any(m => m.Id == eventId), TimeSpan.FromSeconds(30));

        // Wait for all metrics to be recorded
        await WaitForConditionAsync(() =>
        {
            var allMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);
            var retries = allMetrics.Where(m => m.InstrumentName == "ratatoskr.retry.messages").Sum(m => m.Value);
            return retries >= 2;
        }, TimeSpan.FromSeconds(10));

        // 5. Assert Metrics
        var testMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);

        testMetrics.Should().Contain(m => m.InstrumentName == "messaging.client.sent.messages" && m.Value == 1);

        // Should have 3 processing attempts (1 initial + 2 retries)
        var processMetrics = testMetrics.Where(m => m.InstrumentName == "messaging.process.duration").ToList();
        processMetrics.Count.Should().BeGreaterThan(2);

        // Failed attempts should have error.type set
        processMetrics.Should().Contain(m => m.Tags.Any(t => t.Key == "error.type"));
        // Successful attempt should not have error.type
        processMetrics.Should().Contain(m => !m.Tags.Any(t => t.Key == "error.type"));

        // Verify Retries
        var retryMetrics = testMetrics.Where(m => m.InstrumentName == "ratatoskr.retry.messages").ToList();
        retryMetrics.Sum(m => m.Value).Should().Be(2);

        foreach (var retryMetric in retryMetrics)
        {
            AssertMetricTags(retryMetric, ExchangeName, QueueName, RoutingKey);
        }

        // Verify Traces
        var relevantActivities = GetRelevantActivities(activities, eventId);
        var consumerActivities = relevantActivities.Where(a => a.Kind == ActivityKind.Consumer).OrderBy(a => a.StartTimeUtc).ToList();

        consumerActivities.Count.Should().Be(3);

        foreach (var activity in consumerActivities)
        {
            AssertActivityTags(activity, ExchangeName, QueueName, RoutingKey, eventId,
                operationName: "process", operationType: "process");
        }

        // Failed spans should have error status, successful span should not
        consumerActivities.Take(2).Should().AllSatisfy(a =>
            a.Status.Should().Be(ActivityStatusCode.Error));
        consumerActivities.Last().Status.Should().Be(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task Telemetry_Record_DeadLetter()
    {
        // 1. Setup Listeners
        var metricMeasurements = new ConcurrentBag<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var meterListener = CreateMeterListener(metricMeasurements);

        var activities = new ConcurrentBag<Activity>();
        using var activityListener = CreateActivityListener(activities);
        ActivitySource.AddActivityListener(activityListener);

        // 2. Setup Ratatoskr with Always Failing Handler
        var handler = new AlwaysFailingTestEventHandler();

        await StartTestAsync(services => ConfigureRatatoskr(services, handler, useOutbox: false,
            configureConsumer: c => c.WithRetry(2, TimeSpan.FromMilliseconds(100))));

        // 3. Act - Publish Message
        var eventId = "otel-dlq-1";
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = eventId, Data = "dlq-me" }, new MessageProperties { Id = eventId });
        });

        // 4. Wait for processing (DLQ)
        await WaitForConditionAsync(() =>
        {
            var allMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);
            var dlqCount = allMetrics.Where(m => m.InstrumentName == "ratatoskr.dead_letter.messages").Sum(m => m.Value);
            var retryCount = allMetrics.Where(m => m.InstrumentName == "ratatoskr.retry.messages").Sum(m => m.Value);
            return dlqCount >= 1 && retryCount >= 2;
        }, TimeSpan.FromSeconds(30));

        // 5. Assert Metrics
        var testMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);

        // 2 retries recorded
        var retryMetrics = testMetrics.Where(m => m.InstrumentName == "ratatoskr.retry.messages").ToList();
        retryMetrics.Sum(m => m.Value).Should().Be(2);

        // And 1 Dead Letter
        var dlqMetrics = testMetrics.Where(m => m.InstrumentName == "ratatoskr.dead_letter.messages").ToList();
        dlqMetrics.Sum(m => m.Value).Should().Be(1);

        // Process durations should all have error.type set (all failures)
        var processMetrics = testMetrics.Where(m => m.InstrumentName == "messaging.process.duration").ToList();
        processMetrics.Should().AllSatisfy(m =>
            m.Tags.Should().Contain(t => t.Key == "error.type"));

        // Assert Tags
        foreach (var dlqMetric in dlqMetrics)
        {
            AssertMetricTags(dlqMetric, ExchangeName, QueueName, RoutingKey);
        }
        foreach (var retryMetric in retryMetrics)
        {
            AssertMetricTags(retryMetric, ExchangeName, QueueName, RoutingKey);
        }

        // Verify Traces
        var relevantActivities = GetRelevantActivities(activities, eventId);
        var consumerActivities = relevantActivities.Where(a => a.Kind == ActivityKind.Consumer).OrderBy(a => a.StartTimeUtc).ToList();

        consumerActivities.Count.Should().Be(3);

        foreach (var activity in consumerActivities)
        {
            AssertActivityTags(activity, ExchangeName, QueueName, RoutingKey, eventId,
                operationName: "process", operationType: "process");
        }

        // All spans should have error status (all failures)
        consumerActivities.Should().AllSatisfy(a =>
            a.Status.Should().Be(ActivityStatusCode.Error));
    }

    [Test]
    public async Task Metrics_Record_DirectPublish()
    {
        // 1. Setup MeterListener
        var metricMeasurements = new ConcurrentBag<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var meterListener = CreateMeterListener(metricMeasurements);

        // 2. Setup Ratatoskr
        var handler = new TestEventHandler();
        await StartTestAsync(services => ConfigureRatatoskr(services, handler, useOutbox: false));

        // 3. Act - Publish Message
        var eventId = "otel-direct-pub-1";
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = eventId, Data = "direct-metric" });
        });

        // 4. Wait
        await WaitForConditionAsync(() => metricMeasurements.Any(m =>
            m.InstrumentName == "messaging.client.sent.messages" &&
            m.Tags.Any(t => t.Key == "messaging.destination.name" && (string?)t.Value == ExchangeName)),
            TimeSpan.FromSeconds(5));

        // 5. Assert
        var testMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);

        testMetrics.Should().Contain(m => m.InstrumentName == "messaging.client.sent.messages" && m.Value == 1);
        testMetrics.Should().Contain(m => m.InstrumentName == "messaging.client.operation.duration");

        var pubMetric = testMetrics.First(m => m.InstrumentName == "messaging.client.sent.messages");
        AssertMetricTags(pubMetric, ExchangeName, queueName: null, RoutingKey);
    }

    [Test]
    public async Task Metrics_Inbox_RecordsProcessingDuration()
    {
        // 1. Setup MeterListener
        var metricMeasurements = new ConcurrentBag<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var meterListener = CreateMeterListener(metricMeasurements);

        // 2. Setup local transport + inbox (no RabbitMQ needed for this test)
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("otel-inbox-events", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("otel-inbox-events", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<NoOpTestEventHandler>("otel-noop"))
                    .UseInbox<TestDbContext>(inbox => inbox.WithPollingInterval(TimeSpan.FromMilliseconds(500))));
            });
            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // 3. Publish a message so inbox has something to process
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "otel-inbox-1", Data = "inbox metrics" });
        });

        // 4. Wait for inbox processor to run and record metrics
        await WaitForConditionAsync(
            () => metricMeasurements.Any(m => m.InstrumentName == "ratatoskr.inbox.process.duration"),
            TimeSpan.FromSeconds(15));

        // 5. Assert — inbox-specific metrics are recorded (not the outbox ones)
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.inbox.process.duration");
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.inbox.batch.size");
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.inbox.deliver.count"
            && m.Tags.Any(t => t.Key == "status" && (string?)t.Value == "success"));
    }

    [Test]
    public async Task Tracing_Inbox_EmitsDeliverSpan()
    {
        // 1. Setup ActivityListener
        var activities = new ConcurrentBag<Activity>();
        using var listener = CreateActivityListener(activities);
        ActivitySource.AddActivityListener(listener);

        // 2. Setup local transport + inbox
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("otel-inbox-trace", c => c.WithLocal().Produces<TestEvent>());
                bus.AddEventConsumeChannel("otel-inbox-trace", c => c
                    .Consumes<TestEvent>(m => m.WithHandler<NoOpTestEventHandler>("otel-trace-noop"))
                    .UseInbox<TestDbContext>(inbox => inbox.WithPollingInterval(TimeSpan.FromMilliseconds(500))));
            });
            services.AddDbContext<TestDbContext>((sp, opts) =>
                opts.UseNpgsql(PostgresConnectionString));
        });

        await InitializeDatabase();

        // 3. Publish with a known message ID
        var eventId = "otel-inbox-trace-1";
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = eventId, Data = "inbox trace" },
                new MessageProperties { Id = eventId });
        });

        // 4. Wait for the "deliver inbox" span to appear
        await WaitForConditionAsync(
            () => activities.Any(a =>
                a.OperationName == "deliver inbox" &&
                a.TagObjects.Any(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId)),
            TimeSpan.FromSeconds(15));

        // 5. Assert — span has correct kind, system, and handler key
        var deliverActivity = activities.First(a =>
            a.OperationName == "deliver inbox" &&
            a.TagObjects.Any(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId));

        deliverActivity.Kind.Should().Be(ActivityKind.Consumer);
        deliverActivity.TagObjects.Should().Contain(t => t.Key == "messaging.system" && (string?)t.Value == "ratatoskr");
        deliverActivity.TagObjects.Should().Contain(t => t.Key == "messaging.operation.name" && (string?)t.Value == "deliver");
        deliverActivity.TagObjects.Should().Contain(t => t.Key == "messaging.operation.type" && (string?)t.Value == "process");
        deliverActivity.TagObjects.Should().Contain(t => t.Key == "ratatoskr.inbox.handler.key" && (string?)t.Value == "otel-trace-noop");

        // Verify trace context propagation: the deliver span lives in the same trace as the original publish.
        // Filter by message ID to avoid picking up a publish activity from another parallel test.
        var publishActivity = activities.FirstOrDefault(a =>
            a.OperationName == "publish" &&
            a.TagObjects.Any(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId));
        publishActivity.Should().NotBeNull("publish span should exist for the same message");
        deliverActivity.TraceId.Should().Be(publishActivity.TraceId, 
            "inbox delivery must propagate the original message trace ID");

        // Verify no error status on successful delivery
        deliverActivity.Status.Should().Be(ActivityStatusCode.Unset);
    }

    // Helpers

    private void ConfigureRatatoskr<THandler>(IServiceCollection services, THandler handler, bool useOutbox, Action<RabbitMqConsumeOptions>? configureConsumer = null)
        where THandler : class, IMessageHandler<TestEvent>
    {
        services.AddSingleton<THandler>(handler);
        services.AddRatatoskr(bus =>
        {
            bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));

            bus.AddEventPublishChannel(ExchangeName, c => c
                .WithRabbitMq(r => r.WithTopicExchange())
                .Produces<TestEvent>());

            bus.AddEventConsumeChannel(ExchangeName, c => c
                .WithRabbitMq(o =>
                {
                    o.WithQueueName(QueueName).WithAutoAck(false).WithTransientQueue()
                     .WithQueueType(QueueType.Classic);
                    configureConsumer?.Invoke(o);
                })
                .Consumes<TestEvent>(m => m.WithHandler<THandler>()));

            if (useOutbox)
            {
                bus.AddEfCoreOutbox<TestDbContext>(o => o.Options.PollingInterval = TimeSpan.FromSeconds(1));
            }
        });

        services.AddDbContext<TestDbContext>((sp, options) =>
        {
            options.UseNpgsql(PostgresConnectionString);
            if (useOutbox)
            {
                options.RegisterOutbox<TestDbContext>(sp);
            }
        });
    }

    private IEnumerable<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)> GetRelevantMetrics(
        IEnumerable<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)> metrics,
        string exchangeName)
    {
        return metrics.Where(m =>
            m.Tags.Any(t => t.Key == "messaging.destination.name" && (string?)t.Value == exchangeName));
    }

    private MeterListener CreateMeterListener(ConcurrentBag<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)> metricMeasurements)
    {
        var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Ratatoskr")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            metricMeasurements.Add((instrument.Name, measurement, tags.ToArray()));
        });
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            metricMeasurements.Add((instrument.Name, measurement, tags.ToArray()));
        });
        meterListener.Start();
        return meterListener;
    }

    private ActivityListener CreateActivityListener(ConcurrentBag<Activity> activities)
    {
        return new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Ratatoskr",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activities.Add
        };
    }

    private List<Activity> GetRelevantActivities(IEnumerable<Activity> activities, string eventId)
    {
        return activities
            .GroupBy(a => a.TraceId)
            .Where(g => g.Any(a => a.TagObjects.Any(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId)))
            .SelectMany(g => g)
            .OrderBy(a => a.StartTimeUtc)
            .ToList();
    }

    private async Task InitializeDatabase()
    {
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await dbContext.Database.EnsureCreatedAsync();
        });
    }

    private void AssertActivityTags(Activity activity, string exchangeName, string? queueName,
        string routingKey, string messageId, string operationName, string operationType)
    {
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.system" && (string?)t.Value == "rabbitmq");
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.operation.name" && (string?)t.Value == operationName);
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.operation.type" && (string?)t.Value == operationType);
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.destination.name" && (string?)t.Value == exchangeName);

        if (queueName != null)
        {
            activity.TagObjects.Should().Contain(t => t.Key == "messaging.destination.subscription.name" && (string?)t.Value == queueName);
        }

        activity.TagObjects.Should().Contain(t => t.Key == "messaging.rabbitmq.destination.routing_key" && (string?)t.Value == routingKey);
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == messageId);
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.message.body.size");
        activity.TagObjects.Should().Contain(t => t.Key == "server.address");
        activity.TagObjects.Should().Contain(t => t.Key == "server.port");
    }

    private void AssertMetricTags(
        (string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags) metric,
        string exchangeName,
        string? queueName,
        string routingKey)
    {
        metric.Tags.Should().Contain(t => t.Key == "messaging.system" && (string?)t.Value == "rabbitmq");
        metric.Tags.Should().Contain(t => t.Key == "messaging.destination.name" && (string?)t.Value == exchangeName);

        if (queueName != null)
        {
            metric.Tags.Should().Contain(t => t.Key == "messaging.destination.subscription.name" && (string?)t.Value == queueName);
        }

        metric.Tags.Should().Contain(t => t.Key == "messaging.rabbitmq.destination.routing_key" && (string?)t.Value == routingKey);
        metric.Tags.Should().Contain(t => t.Key == "messaging.operation.name");
        metric.Tags.Should().Contain(t => t.Key == "messaging.operation.type");
        metric.Tags.Should().Contain(t => t.Key == "server.address");
        metric.Tags.Should().Contain(t => t.Key == "server.port");
    }

    private class FailingTestEventHandler(int failuresBeforeSuccess) : IMessageHandler<TestEvent>
    {
        private int _attempts;
        public ConcurrentBag<TestEvent> HandledMessages { get; } = new();

        public Task HandleAsync(TestEvent message, MessageProperties context, CancellationToken cancellationToken)
        {
            var currentAttempt = Interlocked.Increment(ref _attempts);

            if (currentAttempt <= failuresBeforeSuccess)
            {
                throw new InvalidOperationException($"Simulated failure {currentAttempt}");
            }

            HandledMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private class AlwaysFailingTestEventHandler : IMessageHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, MessageProperties context, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Always failing");
        }
    }
}
