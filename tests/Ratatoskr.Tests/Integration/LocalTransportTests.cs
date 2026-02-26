using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.Local;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

public class LocalTransportTests : IAsyncDisposable
{
    private IHost? _host;

    private IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host has not been started yet.");

    private async Task StartAsync(Action<IServiceCollection> configure)
    {
        if (_host != null) throw new InvalidOperationException("Host already started.");
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            configure(services);
        });

        _host = builder.Build();
        await _host.StartAsync();
    }

    [Test]
    public async Task LocalTransport_PublishAndConsume_MessageDelivered()
    {
        var handler = new TestEventHandler();

        await StartAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("local.events", c => c
                    .WithLocal()
                    .Produces<TestEvent>());
                bus.AddEventConsumeChannel("local.events", c => c
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        using var scope = Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
        await bus.PublishDirectAsync(new TestEvent { Id = "local-1", Data = "local transport" });

        var dispatched = await session.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched.GetMessage<TestEvent>().Id.Should().Be("local-1");
        dispatched.Result.Should().Be(DispatchResult.Success);
    }

    [Test]
    public async Task LocalTransport_FullPipeline_CapturesAllStages()
    {
        var handler = new TestEventHandler();

        await StartAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("local.events", c => c
                    .WithLocal()
                    .Produces<TestEvent>());
                bus.AddEventConsumeChannel("local.events", c => c
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        using var scope = Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
        await bus.PublishDirectAsync(new TestEvent { Id = "pipeline-1", Data = "full pipeline" });

        // Wait for the full pipeline
        var dispatched = await session.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched.GetMessage<TestEvent>().Id.Should().Be("pipeline-1");

        // Assert all stages captured
        session.Published.Count.Should().BeGreaterThanOrEqualTo(1);
        session.Sent.Count.Should().BeGreaterThanOrEqualTo(1);
        session.Received.Count.Should().BeGreaterThanOrEqualTo(1);
        session.Dispatched.Count.Should().BeGreaterThanOrEqualTo(1);

        // Sent stage should have TransportMessage with local transport metadata
        var sent = session.Sent.Single<TestEvent>();
        sent.TransportMessage.Should().NotBeNull();
        sent.TransportMessage.TransportName.Should().Be("local");

        // Received stage should also have TransportMessage
        var received = session.Received.Single<TestEvent>();
        received.TransportMessage.Should().NotBeNull();
        received.TransportMessage.TransportName.Should().Be("local");
    }

    [Test]
    public async Task LocalTransport_NonBlocking_SenderReturnsBeforeHandlerRuns()
    {
        var tcs = new TaskCompletionSource();
        var handlerStarted = new TaskCompletionSource();

        var handler = new BlockingHandler(handlerStarted, tcs);

        await StartAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("local.events", c => c
                    .WithLocal()
                    .Produces<TestEvent>());
                bus.AddEventConsumeChannel("local.events", c => c
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, BlockingHandler>(handler);
            });
            services.AddRatatoskrTesting();
        });

        // Publish should return before handler runs
        using var scope = Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
        await bus.PublishDirectAsync(new TestEvent { Id = "nonblock-1", Data = "non blocking" });

        // Sender returned - handler may or may not have started, but sender was not blocked
        // Wait for handler to actually start processing
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Unblock the handler
        tcs.SetResult();
    }

    [Test]
    public async Task LocalTransport_MultipleHandlers_AllInvoked()
    {
        var handler1 = new TestEventHandler();
        var handler2 = new SecondTestEventHandler();

        await StartAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("local.events", c => c
                    .WithLocal()
                    .Produces<TestEvent>());
                bus.AddEventConsumeChannel("local.events", c => c
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, TestEventHandler>(handler1);
                bus.AddHandler<TestEvent, SecondTestEventHandler>(handler2);
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        using var scope = Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
        await bus.PublishDirectAsync(new TestEvent { Id = "multi-1", Data = "multiple handlers" });

        var dispatched = await session.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched.Result.Should().Be(DispatchResult.Success);
        handler1.HandledMessages.Should().ContainSingle(m => m.Id == "multi-1");
        handler2.HandledMessages.Should().ContainSingle(m => m.Id == "multi-1");
    }

    [Test]
    public async Task LocalTransport_HandlerFailure_DoesNotCrashConsumer()
    {
        var throwingHandler = new ThrowingTestEventHandler();

        await StartAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("local.events", c => c
                    .WithLocal()
                    .Produces<TestEvent>());
                bus.AddEventConsumeChannel("local.events", c => c
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, ThrowingTestEventHandler>(throwingHandler);
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        // First message - handler throws
        using (var scope = Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "fail-1", Data = "will fail" });
        }

        var dispatched1 = await session.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched1.Result.Should().Be(DispatchResult.RecoverableError);

        // Second message - consumer should still be alive
        await using var session2 = Services.CreateTrackingSession();
        using (var scope = Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "fail-2", Data = "after failure" });
        }

        var dispatched2 = await session2.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched2.Result.Should().Be(DispatchResult.RecoverableError);
    }

    [Test]
    public async Task LocalTransport_FailingSender_DoesNotPreventOtherTransports()
    {
        var handler = new TestEventHandler();
        var failingSender = new FailingMessageSender("failing-transport");

        await StartAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("local.events", c =>
                {
                    c.WithLocal()
                        .Produces<TestEvent>();
                    c.AddTransport("failing-transport");
                });
                bus.AddEventConsumeChannel("local.events", c => c
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
            });
            services.AddSingleton<IMessageSender>(failingSender);
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        using var scope = Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();

        // Publishing should throw AggregateException because the failing sender fails
        var act = () => bus.PublishDirectAsync(new TestEvent { Id = "partial-1", Data = "partial failure" });
        await act.Should().ThrowAsync<AggregateException>();

        // But the local transport should still have received the message
        var dispatched = await session.WaitForDispatched<TestEvent>(TimeSpan.FromSeconds(5));
        dispatched.GetMessage<TestEvent>().Id.Should().Be("partial-1");
        dispatched.Result.Should().Be(DispatchResult.Success);
    }

    [Test]
    public async Task LocalTransport_Shutdown_DrainsBufferedMessages()
    {
        var handledIds = new List<string>();
        var firstMessageReceived = new TaskCompletionSource();
        var releaseHandler = new TaskCompletionSource();
        var drainHandler = new DrainTestHandler(handledIds, firstMessageReceived, releaseHandler);

        IHost? testHost = null;
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("local.events", c => c
                    .WithLocal()
                    .Produces<TestEvent>());
                bus.AddEventConsumeChannel("local.events", c => c
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, DrainTestHandler>(drainHandler);
            });
        });

        testHost = builder.Build();
        await testHost.StartAsync();

        try
        {
            using var scope = testHost.Services.CreateScope();
            var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();

            // Publish 3 messages — first one will block the consumer, buffering the rest
            await bus.PublishDirectAsync(new TestEvent { Id = "drain-1", Data = "first" });
            await bus.PublishDirectAsync(new TestEvent { Id = "drain-2", Data = "second" });
            await bus.PublishDirectAsync(new TestEvent { Id = "drain-3", Data = "third" });

            // Wait until the consumer is actively processing the first message
            await firstMessageReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Start shutdown while messages 2 and 3 are still buffered in the channel
            var stopTask = testHost.StopAsync(TimeSpan.FromSeconds(30));

            // Give shutdown a moment to call Writer.Complete() and base.StopAsync
            await Task.Delay(200);

            // Release the blocked handler — remaining messages should be drained
            releaseHandler.SetResult();

            await stopTask;

            // All 3 messages should have been processed during drain
            handledIds.Should().HaveCount(3);
            handledIds.Should().Contain("drain-1");
            handledIds.Should().Contain("drain-2");
            handledIds.Should().Contain("drain-3");
        }
        finally
        {
            testHost.Dispose();
            _host = null;
        }
    }

    [Test]
    public async Task LocalTransport_TransportMessage_HasCorrectHeaders()
    {
        var handler = new TestEventHandler();

        await StartAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseLocalTransport();
                bus.AddEventPublishChannel("local.events", c => c
                    .WithLocal()
                    .Produces<TestEvent>());
                bus.AddEventConsumeChannel("local.events", c => c
                    .Consumes<TestEvent>());
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
            });
            services.AddRatatoskrTesting();
        });

        await using var session = Services.CreateTrackingSession();

        using var scope = Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
        await bus.PublishDirectAsync(new TestEvent { Id = "headers-1", Data = "header test" });

        var sent = await session.WaitForSent<TestEvent>(TimeSpan.FromSeconds(5));
        sent.TransportMessage.Should().NotBeNull();

        var headers = sent.TransportMessage!.Headers;
        headers.Should().ContainKey("content-type");
        headers.Should().ContainKey("message-id");
        headers.Should().ContainKey("type");
        headers["type"].Should().Be("test.event");

        // Body should contain the serialized message
        var rawJson = Encoding.UTF8.GetString(sent.TransportMessage.Body);
        rawJson.Should().Contain("headers-1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
    }

    /// <summary>
    /// Handler that blocks until released, for testing non-blocking behavior.
    /// </summary>
    private class BlockingHandler(TaskCompletionSource handlerStarted, TaskCompletionSource releaseSignal) : IMessageHandler<TestEvent>
    {
        public async Task HandleAsync(TestEvent message, MessageProperties context, CancellationToken cancellationToken)
        {
            handlerStarted.TrySetResult();
            await releaseSignal.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Handler that blocks on the first message until released, letting subsequent messages buffer.
    /// </summary>
    private class DrainTestHandler(List<string> handled, TaskCompletionSource firstReceived, TaskCompletionSource release) : IMessageHandler<TestEvent>
    {
        private int _count;

        public async Task HandleAsync(TestEvent message, MessageProperties context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                firstReceived.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            lock (handled)
            {
                handled.Add(message.Id);
            }
        }
    }
}
