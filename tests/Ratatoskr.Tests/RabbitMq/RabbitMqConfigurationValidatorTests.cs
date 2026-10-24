using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.RabbitMq;

public class RabbitMqConfigurationValidatorTests
{
    [Test]
    public void AddRatatoskr_ConcurrencyLimitAbovePrefetch_Throws()
    {
        var services = new ServiceCollection();

        var act = () => services.AddRatatoskr(bus => ConfigureConsumer(bus, prefetch: 2, concurrency: 4));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ConcurrencyLimit (4) must be less than or equal to PrefetchCount (2)*");
    }

    [Test]
    [Arguments((ushort)4, (ushort)4)]
    [Arguments((ushort)0, (ushort)8)]
    public void AddRatatoskr_ConcurrencyLimitWithinPrefetch_DoesNotThrow(
        ushort prefetch,
        ushort concurrency
    )
    {
        var services = new ServiceCollection();

        var act = () =>
            services.AddRatatoskr(bus => ConfigureConsumer(bus, prefetch, concurrency));

        act.Should().NotThrow("a prefetch count of 0 means unlimited");
    }

    private static void ConfigureConsumer(RatatoskrBuilder bus, ushort prefetch, ushort concurrency)
    {
        bus.UseRabbitMq(o => o.ConnectionString = new Uri("amqp://localhost"));
        bus.AddEventConsumeChannel(
            "orders",
            c =>
                c.WithRabbitMq(r =>
                        r.WithQueueName("orders-queue")
                            .WithPrefetch(prefetch)
                            .WithConcurrencyLimit(concurrency)
                    )
                    .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
        );
    }
}
