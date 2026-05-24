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

namespace Ratatoskr.Tests.Integration.OpenTelemetry;

public abstract class OpenTelemetryTestBase(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    protected string ExchangeName => $"otel-test-{TestId}";
    protected string QueueName => $"otel-queue-{TestId}";
    protected const string RoutingKey = "test.event";

    protected void ConfigureRatatoskr<THandler>(
        IServiceCollection services,
        THandler handler,
        bool useOutbox,
        Action<RabbitMqConsumeOptions>? configureConsumer = null
    )
        where THandler : class, IMessageHandler<TestEvent>
    {
        services.AddSingleton(handler);
        services.AddRatatoskr(bus =>
        {
            bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));

            bus.AddEventPublishChannel(
                ExchangeName,
                c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
            );

            bus.AddEventConsumeChannel(
                ExchangeName,
                c =>
                    c.WithRabbitMq(o =>
                        {
                            o.WithQueueName(QueueName)
                                .WithAutoAck(false)
                                .WithTransientQueue()
                                .WithQueueType(QueueType.Classic);
                            configureConsumer?.Invoke(o);
                        })
                        .Consumes<TestEvent>(m => m.WithHandler<THandler>())
            );

            if (useOutbox)
            {
                bus.AddEfCoreDurability<TestDbContext>(d =>
                    d.UseOutbox(o => o.Options.PollingInterval = TimeSpan.FromSeconds(1))
                );
            }
        });

        services.AddDbContext<TestDbContext>(
            (sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                if (useOutbox)
                {
                    options.RegisterOutbox<TestDbContext>(sp);
                }
            }
        );
    }

    protected IEnumerable<(
        string InstrumentName,
        double Value,
        KeyValuePair<string, object?>[] Tags
    )> GetRelevantMetrics(
        IEnumerable<(
            string InstrumentName,
            double Value,
            KeyValuePair<string, object?>[] Tags
        )> metrics,
        string exchangeName
    )
    {
        return metrics.Where(m =>
            m.Tags.Any(t =>
                t.Key == "messaging.destination.name" && (string?)t.Value == exchangeName
            )
        );
    }

    protected MeterListener CreateMeterListener(
        ConcurrentBag<(
            string InstrumentName,
            double Value,
            KeyValuePair<string, object?>[] Tags
        )> metricMeasurements
    )
    {
        var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Ratatoskr")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, _) =>
            {
                metricMeasurements.Add((instrument.Name, measurement, tags.ToArray()));
            }
        );
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                metricMeasurements.Add((instrument.Name, measurement, tags.ToArray()));
            }
        );
        meterListener.Start();
        return meterListener;
    }

    protected ActivityListener CreateActivityListener(ConcurrentBag<Activity> activities)
    {
        return new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Ratatoskr",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activities.Add,
        };
    }

    protected List<Activity> GetRelevantActivities(IEnumerable<Activity> activities, string eventId)
    {
        // Filter directly by message ID to isolate activities for this test's message.
        // Trace-based grouping is unreliable when tests run in parallel because a shared
        // ambient Activity.Current can cause multiple tests to share the same TraceId.
        return
        [
            .. activities
                .Where(a =>
                    a.TagObjects.Any(t =>
                        t.Key == "messaging.message.id" && (string?)t.Value == eventId
                    )
                )
                .OrderBy(a => a.StartTimeUtc),
        ];
    }

    protected void AssertActivityTags(
        Activity activity,
        string exchangeName,
        string? queueName,
        string routingKey,
        string messageId,
        string operationName,
        string operationType
    )
    {
        activity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.system" && (string?)t.Value == "rabbitmq");
        activity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.name" && (string?)t.Value == operationName);
        activity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.operation.type" && (string?)t.Value == operationType);
        activity
            .TagObjects.Should()
            .Contain(t =>
                t.Key == "messaging.destination.name" && (string?)t.Value == exchangeName
            );

        if (queueName != null)
        {
            activity
                .TagObjects.Should()
                .Contain(t =>
                    t.Key == "messaging.destination.subscription.name"
                    && (string?)t.Value == queueName
                );
        }

        activity
            .TagObjects.Should()
            .Contain(t =>
                t.Key == "messaging.rabbitmq.destination.routing_key"
                && (string?)t.Value == routingKey
            );
        activity
            .TagObjects.Should()
            .Contain(t => t.Key == "messaging.message.id" && (string?)t.Value == messageId);
        activity.TagObjects.Should().Contain(t => t.Key == "messaging.message.body.size");
        activity.TagObjects.Should().Contain(t => t.Key == "server.address");
        activity.TagObjects.Should().Contain(t => t.Key == "server.port");
    }

    protected void AssertMetricTags(
        (string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags) metric,
        string exchangeName,
        string? queueName,
        string routingKey
    )
    {
        metric
            .Tags.Should()
            .Contain(t => t.Key == "messaging.system" && (string?)t.Value == "rabbitmq");
        metric
            .Tags.Should()
            .Contain(t =>
                t.Key == "messaging.destination.name" && (string?)t.Value == exchangeName
            );

        if (queueName != null)
        {
            metric
                .Tags.Should()
                .Contain(t =>
                    t.Key == "messaging.destination.subscription.name"
                    && (string?)t.Value == queueName
                );
        }

        metric
            .Tags.Should()
            .Contain(t =>
                t.Key == "messaging.rabbitmq.destination.routing_key"
                && (string?)t.Value == routingKey
            );
        metric.Tags.Should().Contain(t => t.Key == "messaging.operation.name");
        metric.Tags.Should().Contain(t => t.Key == "messaging.operation.type");
        metric.Tags.Should().Contain(t => t.Key == "server.address");
        metric.Tags.Should().Contain(t => t.Key == "server.port");
    }
}
