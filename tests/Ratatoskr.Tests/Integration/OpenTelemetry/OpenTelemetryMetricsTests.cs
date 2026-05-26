using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.OpenTelemetry;

public class OpenTelemetryMetricsTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : OpenTelemetryTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Metrics_Are_Recorded_Correctly()
    {
        // 1. Setup MeterListener
        var metricMeasurements =
            new ConcurrentBag<(
                string InstrumentName,
                double Value,
                KeyValuePair<string, object?>[] Tags
            )>();
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
            var initialTraceParent =
                $"00-{ActivityTraceId.CreateRandom().ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";
            dbContext.OutboxMessages.Add(
                new TestEvent { Id = eventId, Data = "trace-me-metrics" },
                new MessageProperties
                {
                    Id = eventId,
                    TraceParent = initialTraceParent,
                    Time = DateTimeOffset.UtcNow.AddSeconds(-1),
                }
            );
            await dbContext.SaveChangesAsync();
        });

        // 4. Wait for processing
        await WaitForConditionAsync(
            () => handler.HandledMessages.Any(m => m.Id == eventId),
            TimeSpan.FromSeconds(10)
        );

        // 5. Assert Metrics
        var testMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);

        // Messaging Metrics (standard OTEL names)
        testMetrics
            .Should()
            .Contain(m => m.InstrumentName == "messaging.client.sent.messages" && m.Value == 1);
        testMetrics
            .Should()
            .Contain(m => m.InstrumentName == "messaging.client.operation.duration");
        testMetrics
            .Should()
            .Contain(m => m.InstrumentName == "messaging.client.consumed.messages" && m.Value == 1);
        testMetrics.Should().Contain(m => m.InstrumentName == "messaging.process.duration");

        // Latency Metrics (custom)
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.receive.lag");
        testMetrics.Should().Contain(m => m.InstrumentName == "ratatoskr.process.lag");

        // Outbox Metrics
        metricMeasurements
            .Should()
            .Contain(m => m.InstrumentName == "ratatoskr.outbox.batch.size" && m.Value >= 1);
        metricMeasurements
            .Should()
            .Contain(m => m.InstrumentName == "ratatoskr.outbox.process.count" && m.Value >= 1);
        metricMeasurements
            .Should()
            .Contain(m => m.InstrumentName == "ratatoskr.outbox.process.duration");

        // Verify no error.type on successful processing
        var processDuration = testMetrics.First(m =>
            m.InstrumentName == "messaging.process.duration"
        );
        processDuration.Tags.Should().NotContain(t => t.Key == "error.type");

        // Verify Retry/DLQ metrics don't exist (no failures)
        testMetrics.Any(m => m.InstrumentName == "ratatoskr.retry.messages").Should().BeFalse();
        testMetrics
            .Any(m => m.InstrumentName == "ratatoskr.dead_letter.messages")
            .Should()
            .BeFalse();

        // Verify Tags on send metrics
        var sendMetric = testMetrics.First(m =>
            m.InstrumentName == "messaging.client.sent.messages"
        );
        AssertMetricTags(sendMetric, ExchangeName, queueName: null, RoutingKey);

        // Verify Tags on consume metrics
        var consumeMetric = testMetrics.First(m =>
            m.InstrumentName == "messaging.client.consumed.messages"
        );
        AssertMetricTags(consumeMetric, ExchangeName, QueueName, RoutingKey);

        // Verify duration is in seconds (should be small values, not hundreds)
        var durationMetric = testMetrics.First(m =>
            m.InstrumentName == "messaging.client.operation.duration"
        );
        durationMetric
            .Value.Should()
            .BeLessThan(30, "duration should be in seconds, not milliseconds");
    }

    [Test]
    public async Task Telemetry_Record_Failures_And_Retries()
    {
        // 1. Setup Listeners
        var metricMeasurements =
            new ConcurrentBag<(
                string InstrumentName,
                double Value,
                KeyValuePair<string, object?>[] Tags
            )>();
        using var meterListener = CreateMeterListener(metricMeasurements);

        var activities = new ConcurrentBag<Activity>();
        using var activityListener = CreateActivityListener(activities);
        ActivitySource.AddActivityListener(activityListener);

        // 2. Setup Ratatoskr with a Failing Handler (fails 2 times)
        var handler = new FailingTestEventHandler(failuresBeforeSuccess: 2);

        await StartTestAsync(services =>
            ConfigureRatatoskr(
                services,
                handler,
                useOutbox: false,
                configureConsumer: c => c.WithRetry(3, TimeSpan.FromMilliseconds(100))
            )
        );

        // 3. Act - Publish Message
        var eventId = "otel-retry-1";
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = eventId, Data = "retry-me" },
                new MessageProperties { Id = eventId }
            );
        });

        // 4. Wait for processing (Success)
        await WaitForConditionAsync(
            () => handler.HandledMessages.Any(m => m.Id == eventId),
            TimeSpan.FromSeconds(30)
        );

        // Wait for all metrics to be recorded
        await WaitForConditionAsync(
            () =>
            {
                var allMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);
                var retries = allMetrics
                    .Where(m => m.InstrumentName == "ratatoskr.retry.messages")
                    .Sum(m => m.Value);
                return retries >= 2;
            },
            TimeSpan.FromSeconds(10)
        );

        // 5. Assert Metrics
        var testMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);

        testMetrics
            .Should()
            .Contain(m => m.InstrumentName == "messaging.client.sent.messages" && m.Value == 1);

        // Should have 3 processing attempts (1 initial + 2 retries)
        var processMetrics = testMetrics
            .Where(m => m.InstrumentName == "messaging.process.duration")
            .ToList();
        processMetrics.Count.Should().BeGreaterThan(2);

        // Failed attempts should have error.type set
        processMetrics.Should().Contain(m => m.Tags.Any(t => t.Key == "error.type"));
        // Successful attempt should not have error.type
        processMetrics.Should().Contain(m => !m.Tags.Any(t => t.Key == "error.type"));

        // Verify Retries
        var retryMetrics = testMetrics
            .Where(m => m.InstrumentName == "ratatoskr.retry.messages")
            .ToList();
        retryMetrics.Sum(m => m.Value).Should().Be(2);

        foreach (var retryMetric in retryMetrics)
        {
            AssertMetricTags(retryMetric, ExchangeName, QueueName, RoutingKey);
        }

        // Verify Traces — filter to RabbitMQ receive spans only (dispatch spans are also Consumer kind)
        var relevantActivities = GetRelevantActivities(activities, eventId);
        var consumerActivities = relevantActivities
            .Where(a =>
                a.Kind == ActivityKind.Consumer
                && a.TagObjects.Any(t =>
                    t.Key == "messaging.system" && (string?)t.Value == "rabbitmq"
                )
                && a.TagObjects.Any(t =>
                    t.Key == "messaging.message.id" && (string?)t.Value == eventId
                )
            )
            .OrderBy(a => a.StartTimeUtc)
            .ToList();

        consumerActivities.Count.Should().Be(3);

        foreach (var activity in consumerActivities)
        {
            AssertActivityTags(
                activity,
                ExchangeName,
                QueueName,
                RoutingKey,
                eventId,
                operationName: "process",
                operationType: "process"
            );
        }

        // Failed spans should have error status, successful span should not
        consumerActivities
            .Take(2)
            .Should()
            .AllSatisfy(a => a.Status.Should().Be(ActivityStatusCode.Error));
        consumerActivities.Last().Status.Should().Be(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task Telemetry_Record_DeadLetter()
    {
        // 1. Setup Listeners
        var metricMeasurements =
            new ConcurrentBag<(
                string InstrumentName,
                double Value,
                KeyValuePair<string, object?>[] Tags
            )>();
        using var meterListener = CreateMeterListener(metricMeasurements);

        var activities = new ConcurrentBag<Activity>();
        using var activityListener = CreateActivityListener(activities);
        ActivitySource.AddActivityListener(activityListener);

        // 2. Setup Ratatoskr with Always Failing Handler
        var handler = new AlwaysFailingTestEventHandler();

        await StartTestAsync(services =>
            ConfigureRatatoskr(
                services,
                handler,
                useOutbox: false,
                configureConsumer: c => c.WithRetry(2, TimeSpan.FromMilliseconds(100))
            )
        );

        // 3. Act - Publish Message
        var eventId = "otel-dlq-1";
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = eventId, Data = "dlq-me" },
                new MessageProperties { Id = eventId }
            );
        });

        // 4. Wait for processing (DLQ)
        await WaitForConditionAsync(
            () =>
            {
                var allMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);
                var dlqCount = allMetrics
                    .Where(m => m.InstrumentName == "ratatoskr.dead_letter.messages")
                    .Sum(m => m.Value);
                var retryCount = allMetrics
                    .Where(m => m.InstrumentName == "ratatoskr.retry.messages")
                    .Sum(m => m.Value);
                return dlqCount >= 1 && retryCount >= 2;
            },
            TimeSpan.FromSeconds(30)
        );

        // 5. Assert Metrics
        var testMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);

        // 2 retries recorded
        var retryMetrics = testMetrics
            .Where(m => m.InstrumentName == "ratatoskr.retry.messages")
            .ToList();
        retryMetrics.Sum(m => m.Value).Should().Be(2);

        // And 1 Dead Letter
        var dlqMetrics = testMetrics
            .Where(m => m.InstrumentName == "ratatoskr.dead_letter.messages")
            .ToList();
        dlqMetrics.Sum(m => m.Value).Should().Be(1);

        // Process durations should all have error.type set (all failures)
        var processMetrics = testMetrics
            .Where(m => m.InstrumentName == "messaging.process.duration")
            .ToList();
        processMetrics
            .Should()
            .AllSatisfy(m => m.Tags.Should().Contain(t => t.Key == "error.type"));

        // Assert Tags
        foreach (var dlqMetric in dlqMetrics)
        {
            AssertMetricTags(dlqMetric, ExchangeName, QueueName, RoutingKey);
        }
        foreach (var retryMetric in retryMetrics)
        {
            AssertMetricTags(retryMetric, ExchangeName, QueueName, RoutingKey);
        }

        // Verify Traces — filter to RabbitMQ receive spans only (dispatch spans are also Consumer kind)
        var relevantActivities = GetRelevantActivities(activities, eventId);
        var consumerActivities = relevantActivities
            .Where(a =>
                a.Kind == ActivityKind.Consumer
                && a.TagObjects.Any(t =>
                    t.Key == "messaging.system" && (string?)t.Value == "rabbitmq"
                )
                && a.TagObjects.Any(t =>
                    t.Key == "messaging.message.id" && (string?)t.Value == eventId
                )
            )
            .OrderBy(a => a.StartTimeUtc)
            .ToList();

        consumerActivities.Count.Should().Be(3);

        foreach (var activity in consumerActivities)
        {
            AssertActivityTags(
                activity,
                ExchangeName,
                QueueName,
                RoutingKey,
                eventId,
                operationName: "process",
                operationType: "process"
            );
        }

        // All spans should have error status (all failures)
        consumerActivities
            .Should()
            .AllSatisfy(a => a.Status.Should().Be(ActivityStatusCode.Error));
    }

    [Test]
    public async Task Metrics_Record_DirectPublish()
    {
        // 1. Setup MeterListener
        var metricMeasurements =
            new ConcurrentBag<(
                string InstrumentName,
                double Value,
                KeyValuePair<string, object?>[] Tags
            )>();
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
        await WaitForConditionAsync(
            () =>
                metricMeasurements.Any(m =>
                    m.InstrumentName == "messaging.client.sent.messages"
                    && m.Tags.Any(t =>
                        t.Key == "messaging.destination.name" && (string?)t.Value == ExchangeName
                    )
                ),
            TimeSpan.FromSeconds(5)
        );

        // 5. Assert
        var testMetrics = GetRelevantMetrics(metricMeasurements, ExchangeName);

        testMetrics
            .Should()
            .Contain(m => m.InstrumentName == "messaging.client.sent.messages" && m.Value == 1);
        testMetrics
            .Should()
            .Contain(m => m.InstrumentName == "messaging.client.operation.duration");

        var pubMetric = testMetrics.First(m =>
            m.InstrumentName == "messaging.client.sent.messages"
        );
        AssertMetricTags(pubMetric, ExchangeName, queueName: null, RoutingKey);
    }

    [Test]
    public async Task Metrics_Inbox_RecordsProcessingDuration()
    {
        // 1. Setup MeterListener
        var metricMeasurements =
            new ConcurrentBag<(
                string InstrumentName,
                double Value,
                KeyValuePair<string, object?>[] Tags
            )>();
        using var meterListener = CreateMeterListener(metricMeasurements);

        // 2. Setup EF Core transport + inbox (no RabbitMQ needed for this test)
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEventPublishChannel(
                    "otel-inbox-events",
                    c => c.WithEfCore().Produces<TestEvent>()
                );
                bus.AddEventConsumeChannel(
                    "otel-inbox-events",
                    c =>
                        c.Consumes<TestEvent>(m => m.WithHandler<NoOpTestEventHandler>("otel-noop"))
                            .UseInbox<TestDbContext>()
                );
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseInbox(inbox => inbox.WithoutBackgroundProcessing())
                );
            });
            services.AddDbContext<TestDbContext>(
                (sp, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();

        // 3. Publish a message so inbox has something to process
        await InScopeAsync(async ctx =>
        {
            var bus = ctx.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(
                new TestEvent { Id = "otel-inbox-1", Data = "inbox metrics" }
            );
        });

        // 4. Process inbox deterministically instead of relying on background polling
        await InScopeAsync(async ctx =>
        {
            using var scope = ctx.ServiceProvider.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<
                InboxMessageProcessor<TestDbContext>
            >();
            await processor.ProcessBatchAsync(
                includeStuckMessageDetection: false,
                CancellationToken.None
            );
        });

        // 5. Assert — inbox-specific metrics are recorded (not the outbox ones)
        metricMeasurements
            .Should()
            .Contain(m => m.InstrumentName == "ratatoskr.inbox.process.duration");
        metricMeasurements.Should().Contain(m => m.InstrumentName == "ratatoskr.inbox.batch.size");
        metricMeasurements
            .Should()
            .Contain(m =>
                m.InstrumentName == "ratatoskr.inbox.deliver.count"
                && m.Tags.Any(t => t.Key == "status" && (string?)t.Value == "success")
            );
    }

    private class FailingTestEventHandler(int failuresBeforeSuccess) : IMessageHandler<TestEvent>
    {
        private int _attempts;
        public ConcurrentBag<TestEvent> HandledMessages { get; } = new();

        public Task HandleAsync(
            TestEvent message,
            MessageProperties context,
            CancellationToken cancellationToken
        )
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
        public Task HandleAsync(
            TestEvent message,
            MessageProperties context,
            CancellationToken cancellationToken
        )
        {
            throw new InvalidOperationException("Always failing");
        }
    }
}
