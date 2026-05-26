using Ratatoskr.Config;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.MessageTracking;

public abstract class MessageTrackingTestBase(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    protected string QueueName => $"track-queue-{TestId}";
    protected string ExchangeName => $"track-exchange-{TestId}";
    protected static string DefaultRoutingKey => "test.event";

    protected void ConfigureConsumeBus(
        RatatoskrBuilder bus,
        Action<MessageConsumptionBuilder<TestEvent>>? configureHandler = null
    )
    {
        bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
        bus.AddCommandPublishChannel(
            QueueName,
            c => c.WithRabbitMq(r => r.WithTopicExchange()).Produces<TestEvent>()
        );
        bus.AddCommandConsumeChannel(
            QueueName,
            c =>
                c.WithRabbitMq(o =>
                        o.WithQueueName(QueueName)
                            .WithAutoAck(false)
                            .WithTransientQueue()
                            .WithQueueType(QueueType.Classic)
                    )
                    .Consumes(configureHandler ?? (_ => { }))
        );
    }
}
