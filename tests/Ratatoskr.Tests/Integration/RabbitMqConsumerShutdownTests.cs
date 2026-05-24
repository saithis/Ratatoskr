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

    private readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public void Release() => _release.TrySetResult();

    public async Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
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
    private readonly TaskCompletionSource _processedAll = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public ConcurrentTrackingTestEventHandler(int targetCount)
    {
        _targetCount = targetCount;
    }

    public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

    public Task WaitUntilProcessedAsync(TimeSpan timeout)
    {
        return _processedAll.Task.WaitAsync(timeout);
    }

    public async Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
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

            if (
                Interlocked.CompareExchange(ref _maxConcurrency, nowConcurrent, snapshot)
                == snapshot
            )
            {
                return;
            }
        }
    }
}

/// <summary>
/// All handlers wait until the last one has started, then all complete simultaneously —
/// maximising concurrent-ack pressure on the shared consume channel.
/// </summary>
file sealed class ConcurrentAckStressEventHandler : IMessageHandler<TestEvent>
{
    private readonly int _total;
    private int _arrived;
    private readonly TaskCompletionSource _allArrived = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _processed;
    private readonly TaskCompletionSource _processedAll = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public ConcurrentAckStressEventHandler(int total) => _total = total;

    public Task WaitUntilAllProcessedAsync(TimeSpan timeout) =>
        _processedAll.Task.WaitAsync(timeout);

    public async Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
    {
        if (Interlocked.Increment(ref _arrived) >= _total)
        {
            _allArrived.TrySetResult();
        }

        await _allArrived.Task.WaitAsync(cancellationToken);

        if (Interlocked.Increment(ref _processed) >= _total)
        {
            _processedAll.TrySetResult();
        }
    }
}

file sealed class BackpressureTestEventHandler : IMessageHandler<TestEvent>
{
    private int _activeCount;
    private readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public int ActiveCount => Volatile.Read(ref _activeCount);

    public void Release() => _release.TrySetResult();

    public async Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _activeCount);
        try
        {
            await _release.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCount);
        }
    }
}

public class RabbitMqConsumerShutdownTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string QueueName => $"shutdown-drain-{TestId}";
    private string ConcurrencyQueueName => $"shutdown-concurrency-{TestId}";
    private string BackpressureQueueName => $"shutdown-backpressure-{TestId}";
    private string AckStressQueueName => $"ack-stress-{TestId}";
    private string PublishStressQueueName => $"publish-stress-{TestId}";

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
                bus.AddCommandConsumeChannel(
                    QueueName,
                    c =>
                    {
                        c.WithRabbitMq(o =>
                            o.WithQueueName(QueueName)
                                .WithAutoAck(false)
                                .WithTransientQueue()
                                .WithQueueType(QueueType.Classic)
                        );
                        c.Consumes<TestEvent>(m => m.WithHandler<DrainGateTestEventHandler>());
                    }
                );
            });
        });

        var host = Services.GetRequiredService<IHost>();

        await PublishToRabbitMqAsync(
            exchange: "",
            routingKey: QueueName,
            new TestEvent { Id = "drain", Data = "x" }
        );

        await WaitForConditionAsync(
            () => handler.HasEntered,
            TimeSpan.FromSeconds(10),
            "Handler should start processing"
        );

        var stopTask = host.StopAsync(CancellationToken.None);
        await Task.Delay(50);
        handler.Release();

        await stopTask.WaitAsync(TimeSpan.FromSeconds(60));

        (await GetMessageCountAsync(QueueName))
            .Should()
            .Be(0, "message should be acked after graceful drain, not left unacked on the queue");
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
                bus.AddCommandConsumeChannel(
                    ConcurrencyQueueName,
                    c =>
                    {
                        c.WithRabbitMq(o =>
                            o.WithQueueName(ConcurrencyQueueName)
                                .WithAutoAck(false)
                                .WithPrefetch(6)
                                .WithConcurrencyLimit(3)
                                .WithTransientQueue()
                                .WithQueueType(QueueType.Classic)
                        );
                        c.Consumes<TestEvent>(m =>
                            m.WithHandler<ConcurrentTrackingTestEventHandler>()
                        );
                    }
                );
            });
        });

        for (var i = 0; i < totalMessages; i++)
        {
            await PublishToRabbitMqAsync(
                exchange: "",
                routingKey: ConcurrencyQueueName,
                new TestEvent { Id = $"c-{i}", Data = "x" }
            );
        }

        await handler.WaitUntilProcessedAsync(TimeSpan.FromSeconds(20));

        handler
            .MaxConcurrency.Should()
            .BeGreaterThan(1, "handlers should run concurrently when ConcurrencyLimit > 1");
        handler
            .MaxConcurrency.Should()
            .BeLessThanOrEqualTo(3, "concurrency should be capped by ConcurrencyLimit");

        // WaitUntilProcessedAsync fires when the handler returns, but BasicAckAsync runs
        // after that in RabbitMqConsumer. Poll until the broker confirms all acks.
        await WaitForConditionAsync(
            async () => (await GetMessageCountAsync(ConcurrencyQueueName)) == 0,
            TimeSpan.FromSeconds(10),
            "All messages should be acked on the queue"
        );
    }

    [Test]
    public async Task Consumer_WithPrefetchEqualToConcurrencyLimit_HoldsExtraMessagesInQueue()
    {
        const int concurrencyLimit = 2;
        var handler = new BackpressureTestEventHandler();

        await StartTestAsync(services =>
        {
            services.AddSingleton(handler);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(
                    BackpressureQueueName,
                    c =>
                    {
                        c.WithRabbitMq(o =>
                            o.WithQueueName(BackpressureQueueName)
                                .WithAutoAck(false)
                                .WithPrefetch(concurrencyLimit)
                                .WithConcurrencyLimit(concurrencyLimit)
                                .WithTransientQueue()
                                .WithQueueType(QueueType.Classic)
                        );
                        c.Consumes<TestEvent>(m => m.WithHandler<BackpressureTestEventHandler>());
                    }
                );
            });
        });

        for (var i = 0; i < 4; i++)
        {
            await PublishToRabbitMqAsync(
                exchange: "",
                routingKey: BackpressureQueueName,
                new TestEvent { Id = $"bp-{i}", Data = "x" }
            );
        }

        await WaitForConditionAsync(
            () => handler.ActiveCount == concurrencyLimit,
            TimeSpan.FromSeconds(10),
            $"Expected {concurrencyLimit} handlers running concurrently"
        );

        (await GetMessageCountAsync(BackpressureQueueName))
            .Should()
            .Be(2, "messages beyond prefetch should remain in the queue, not accumulate in memory");

        handler.Release();

        await WaitForConditionAsync(
            async () => (await GetMessageCountAsync(BackpressureQueueName)) == 0,
            TimeSpan.FromSeconds(10),
            "All messages should be acked after handlers are released"
        );
    }

    [Test]
    public async Task Consumer_ConcurrentAck_DoesNotCauseChannelExceptions()
    {
        const int totalMessages = 8;
        var handler = new ConcurrentAckStressEventHandler(totalMessages);

        await StartTestAsync(services =>
        {
            services.AddSingleton(handler);
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(
                    AckStressQueueName,
                    c =>
                    {
                        c.WithRabbitMq(o =>
                            o.WithQueueName(AckStressQueueName)
                                .WithAutoAck(false)
                                .WithPrefetch(totalMessages)
                                .WithConcurrencyLimit(totalMessages)
                                .WithTransientQueue()
                                .WithQueueType(QueueType.Classic)
                        );
                        c.Consumes<TestEvent>(m =>
                            m.WithHandler<ConcurrentAckStressEventHandler>()
                        );
                    }
                );
            });
        });

        for (var i = 0; i < totalMessages; i++)
        {
            await PublishToRabbitMqAsync(
                exchange: "",
                routingKey: AckStressQueueName,
                new TestEvent { Id = $"ack-{i}", Data = "x" }
            );
        }

        await handler.WaitUntilAllProcessedAsync(TimeSpan.FromSeconds(20));

        await WaitForConditionAsync(
            async () => (await GetMessageCountAsync(AckStressQueueName)) == 0,
            TimeSpan.FromSeconds(10),
            "All messages should be acked when handlers complete simultaneously"
        );
    }

    [Test]
    public async Task Sender_ConcurrentPublish_DoesNotCorruptChannel()
    {
        const int messageCount = 20;

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
            });
        });

        await EnsureQueueBoundAsync(PublishStressQueueName, "", "");

        var sender = Services.GetRequiredService<IMessageSender>();
        var body = "{}"u8.ToArray();

        var tasks = Enumerable
            .Range(0, messageCount)
            .Select(_ =>
                sender.SendAsync(
                    body,
                    new MessageProperties
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = "test.event",
                        Source = "/test",
                        Time = DateTimeOffset.UtcNow,
                    }.SetRoutingKey(PublishStressQueueName),
                    CancellationToken.None
                )
            );

        await Task.WhenAll(tasks);

        await WaitForConditionAsync(
            async () => (await GetMessageCountAsync(PublishStressQueueName)) == messageCount,
            TimeSpan.FromSeconds(10),
            "All concurrently published messages should arrive in the queue"
        );
    }
}
