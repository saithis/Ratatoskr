using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Integration;

/// <summary>
/// Blocks until <see cref="Release"/> is called, so shutdown can be exercised while a handler is in flight.
/// </summary>
file sealed class DrainGateTestEventHandler : IMessageHandler<TestEvent>
{
    private int _entered;

    public bool HasEntered => Volatile.Read(ref _entered) != 0;

    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _release.TrySetResult();

    public async Task HandleAsync(TestEvent message, MessageProperties context, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _entered, 1);
        await _release.Task;
    }
}

public class RabbitMqConsumerShutdownTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string QueueName => $"shutdown-drain-{TestId}";

    [Test]
    public async Task StopAsync_WaitsForInFlightHandler_BeforeClosingChannels()
    {
        var handler = new DrainGateTestEventHandler();
        await StartTestAsync(services =>
        {
            services.AddSingleton(handler);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o =>
                {
                    o.ConnectionString = new Uri(RabbitMqConnectionString);
                    o.ShutdownDrainTimeout = TimeSpan.FromSeconds(30);
                });
                bus.AddCommandConsumeChannel(QueueName, c =>
                {
                    c.WithRabbitMq(o => o.WithQueueName(QueueName).WithAutoAck(false).WithTransientQueue()
                        .WithQueueType(QueueType.Classic));
                    c.Consumes<TestEvent>(m => m.WithHandler<DrainGateTestEventHandler>());
                });
            });
        });

        var host = Services.GetRequiredService<IHost>();

        await PublishToRabbitMqAsync(exchange: "", routingKey: QueueName, new TestEvent { Id = "drain", Data = "x" });

        await WaitForConditionAsync(() => handler.HasEntered, TimeSpan.FromSeconds(10), "Handler should start processing");

        var stopTask = host.StopAsync(CancellationToken.None);
        await Task.Delay(50);
        handler.Release();

        await stopTask.WaitAsync(TimeSpan.FromSeconds(60));

        (await GetMessageCountAsync(QueueName)).Should().Be(0, "message should be acked after graceful drain, not left unacked on the queue");
    }
}
