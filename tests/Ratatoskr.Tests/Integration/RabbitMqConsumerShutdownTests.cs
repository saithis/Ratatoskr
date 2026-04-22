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

file sealed class ConcurrentTrackingTestEventHandler : IMessageHandler<TestEvent>
{
    private readonly int _targetCount;
    private int _currentConcurrency;
    private int _maxConcurrency;
    private int _processed;
    private readonly TaskCompletionSource _processedAll = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentTrackingTestEventHandler(int targetCount)
    {
        _targetCount = targetCount;
    }

    public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

    public Task WaitUntilProcessedAsync(TimeSpan timeout)
    {
        return _processedAll.Task.WaitAsync(timeout);
    }

    public async Task HandleAsync(TestEvent message, MessageProperties context, CancellationToken cancellationToken)
    {
        var nowConcurrent = Interlocked.Increment(ref _currentConcurrency);
        UpdateMaxConcurrency(nowConcurrent);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _currentConcurrency);
            if (Interlocked.Increment(ref _processed) >= _targetCount)
            {
                _processedAll.TrySetResult();
            }
        }
    }

    private void UpdateMaxConcurrency(int nowConcurrent)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _maxConcurrency);
            if (snapshot >= nowConcurrent)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maxConcurrency, nowConcurrent, snapshot) == snapshot)
            {
                return;
            }
        }
    }
}

public class RabbitMqConsumerShutdownTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string QueueName => $"shutdown-drain-{TestId}";
    private string ConcurrencyQueueName => $"shutdown-concurrency-{TestId}";

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

    [Test]
    public async Task Consumer_UsesConfiguredConcurrencyLimit_ForParallelHandlers()
    {
        const int totalMessages = 6;
        var handler = new ConcurrentTrackingTestEventHandler(totalMessages);

        await StartTestAsync(services =>
        {
            services.AddSingleton(handler);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(ConcurrencyQueueName, c =>
                {
                    c.WithRabbitMq(o => o
                        .WithQueueName(ConcurrencyQueueName)
                        .WithAutoAck(false)
                        .WithPrefetch(6)
                        .WithConcurrencyLimit(3)
                        .WithTransientQueue()
                        .WithQueueType(QueueType.Classic));
                    c.Consumes<TestEvent>(m => m.WithHandler<ConcurrentTrackingTestEventHandler>());
                });
            });
        });

        for (var i = 0; i < totalMessages; i++)
        {
            await PublishToRabbitMqAsync(exchange: "", routingKey: ConcurrencyQueueName, new TestEvent { Id = $"c-{i}", Data = "x" });
        }

        await handler.WaitUntilProcessedAsync(TimeSpan.FromSeconds(20));

        handler.MaxConcurrency.Should().BeGreaterThan(1, "handlers should run concurrently when ConcurrencyLimit > 1");
        handler.MaxConcurrency.Should().BeLessThanOrEqualTo(3, "concurrency should be capped by ConcurrencyLimit");

        // WaitUntilProcessedAsync fires when the handler returns, but BasicAckAsync runs
        // after that in RabbitMqConsumer. Poll until the broker confirms all acks.
        await WaitForConditionAsync(
            async () => (await GetMessageCountAsync(ConcurrencyQueueName)) == 0,
            TimeSpan.FromSeconds(10),
            "All messages should be acked on the queue");
    }
}
