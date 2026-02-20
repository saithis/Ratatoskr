using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
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

        var outboxActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "Ratatoskr.OutboxProcess");
        var sendActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "Ratatoskr.Send");
        var receiveActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "Ratatoskr.Receive");

        outboxActivity.Should().NotBeNull("OutboxProcess activity should exist");
        sendActivity.Should().NotBeNull("Send activity should exist");
        receiveActivity.Should().NotBeNull("Receive activity should exist");

        // Verify Hierarchy
        sendActivity!.ParentId.Should().Be(outboxActivity!.Id);
        receiveActivity!.ParentId.Should().Be(sendActivity.Id);

        // Verify Kinds
        outboxActivity.Kind.Should().Be(ActivityKind.Producer);
        sendActivity.Kind.Should().Be(ActivityKind.Client);
        receiveActivity.Kind.Should().Be(ActivityKind.Consumer);

        // Verify Tags
        AssertActivityTags(receiveActivity, ExchangeName, QueueName, RoutingKey, eventId);
        AssertActivityTags(sendActivity, ExchangeName, queueName: null, RoutingKey, eventId);

        outboxActivity.TagObjects.Should().Contain(t => t.Key == "messaging.system" && (string?)t.Value == "ratatoskr");
        outboxActivity.TagObjects.Should().Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId);
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

        var publishActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "Ratatoskr.Publish");
        var sendActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "Ratatoskr.Send");
        var receiveActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "Ratatoskr.Receive");
        var outboxActivity = relevantActivities.FirstOrDefault(a => a.OperationName == "Ratatoskr.OutboxProcess");

        publishActivity.Should().NotBeNull("Publish activity should exist");
        sendActivity.Should().NotBeNull("Send activity should exist");
        receiveActivity.Should().NotBeNull("Receive activity should exist");
        outboxActivity.Should().BeNull("OutboxProcess activity should NOT exist");

        // Verify Hierarchy
        sendActivity!.ParentId.Should().Be(publishActivity!.Id);
        receiveActivity!.ParentId.Should().Be(sendActivity.Id);

        // Verify Kinds
        publishActivity.Kind.Should().Be(ActivityKind.Producer);
        sendActivity.Kind.Should().Be(ActivityKind.Client);
        receiveActivity.Kind.Should().Be(ActivityKind.Consumer);

        // Verify Tags
        AssertActivityTags(receiveActivity, ExchangeName, QueueName, RoutingKey, eventId);
        AssertActivityTags(sendActivity, ExchangeName, queueName: null, RoutingKey, eventId);

        publishActivity.TagObjects.Should().Contain(t => t.Key == "messaging.system" && (string?)t.Value == "ratatoskr");
        publishActivity.TagObjects.Should().Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == eventId);
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

        // Messaging Metrics
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.publish.messages" && m.Value == 1);
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.publish.duration");
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.receive.messages" && m.Value == 1);
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.process.messages" && m.Value == 1);
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.process.duration");

        // Latency Metrics
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.receive.lag");
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.process.lag");

        // Outbox Metrics
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.outbox.batch.size" && m.Value >= 1);
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.outbox.process.count" && m.Value >= 1);
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.outbox.process.duration");

        // Verify Process Outcome
        var processMetric = testMetrics.First(m => m.InstrumentName == "ratatoskr.process.messages");
        AssertMetricTags(processMetric, ExchangeName, QueueName, RoutingKey, outcome: "success");

        // Verify Retry/DLQ metrics exist (should be empty/false)
        testMetrics.Any(m => m.InstrumentName == "ratatoskr.retry.messages").Should().BeFalse();
        testMetrics.Any(m => m.InstrumentName == "ratatoskr.dead_letter.messages").Should().BeFalse();

        // Verify Tags
        var publishMetric = testMetrics.First(m => m.InstrumentName == "ratatoskr.publish.messages");
        AssertMetricTags(publishMetric, ExchangeName, queueName: null, RoutingKey);

        var receiveMetric = testMetrics.First(m => m.InstrumentName == "ratatoskr.receive.messages");
        AssertMetricTags(receiveMetric, ExchangeName, QueueName, RoutingKey);
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
        
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.publish.messages" && m.Value == 1);
        
        // Should have 3 processing attempts (1 initial + 2 retries)
        var processMetrics = testMetrics.Where(m => m.InstrumentName == "ratatoskr.process.messages").ToList();
        processMetrics.Count.Should().BeGreaterThan(2); 
        
        processMetrics.Should().Contain(m => m.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == "failure"));
        processMetrics.Should().Contain(m => m.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == "success"));

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
            AssertActivityTags(activity, ExchangeName, QueueName, RoutingKey, eventId);
        }
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

        // Process outcome should be all failures
        var processMetrics = testMetrics.Where(m => m.InstrumentName == "ratatoskr.process.messages").ToList();
        processMetrics.Should().AllSatisfy(m => 
            m.Tags.Should().Contain(t => t.Key == "outcome" && (string?)t.Value == "failure"));
            
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
            AssertActivityTags(activity, ExchangeName, QueueName, RoutingKey, eventId);
        }
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
            m.InstrumentName == "ratatoskr.publish.messages" &&
            m.Tags.Any(t => t.Key == "messaging.destination.name" && (string?)t.Value == ExchangeName)), 
            TimeSpan.FromSeconds(5));
        
        // 5. Assert
        var testMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);

        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.publish.messages" && m.Value == 1);
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.publish.duration");

        var pubMetric = testMetrics.First(m => m.InstrumentName == "ratatoskr.publish.messages");
        AssertMetricTags(pubMetric, ExchangeName, queueName: null, RoutingKey);
    }

    // Helpers

    private void ConfigureRatatoskr<THandler>(IServiceCollection services, THandler handler, bool useOutbox, Action<RabbitMqChannelOptions>? configureConsumer = null)
        where THandler : class, IMessageHandler<TestEvent>
    {
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
                .Consumes<TestEvent>());

            bus.AddHandler<TestEvent, THandler>(handler);

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

    private void AssertActivityTags(Activity activity, string exchangeName, string? queueName, string routingKey, string messageId)
    {
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.system" && (string?)t.Value == "rabbitmq");
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.destination.name" && (string?)t.Value == exchangeName);
        
        if (queueName != null)
        {
            activity.TagObjects.Should().Contain(t => t.Key == "messaging.destination.subscription.name" && (string?)t.Value == queueName);
        }
            
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.rabbitmq.destination.routing_key" && (string?)t.Value == routingKey);
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == messageId);
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.message.body.size");
    }

    private void AssertMetricTags(
        (string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags) metric,
        string exchangeName,
        string? queueName,
        string routingKey,
        string? outcome = null)
    {
        metric.Tags.Should().Contain(t => t.Key == "messaging.system" && (string?)t.Value == "rabbitmq");
        metric.Tags.Should().Contain(t => t.Key == "messaging.destination.name" && (string?)t.Value == exchangeName);
        
        if (queueName != null)
        {
            metric.Tags.Should().Contain(t => t.Key == "messaging.destination.subscription.name" && (string?)t.Value == queueName);
        }

        metric.Tags.Should().Contain(t => t.Key == "messaging.rabbitmq.destination.routing_key" && (string?)t.Value == routingKey);
        
        if (outcome != null)
        {
            metric.Tags.Should().Contain(t => t.Key == "outcome" && (string?)t.Value == outcome);
        }
    }

    private class FailingTestEventHandler(int failuresBeforeSuccess) : IMessageHandler<TestEvent>
    {
        private int _attempts = 0;
        private readonly object _lock = new();
        public List<TestEvent> HandledMessages { get; } = new();

        public Task HandleAsync(TestEvent message, MessageProperties context, CancellationToken cancellationToken)
        {
            int currentAttempt;
            lock (_lock)
            {
                _attempts++;
                currentAttempt = _attempts;
            }
            
            if (currentAttempt <= failuresBeforeSuccess)
            {
                throw new InvalidOperationException($"Simulated failure {currentAttempt}");
            }
            
            lock (_lock)
            {
                HandledMessages.Add(message);
            }
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
