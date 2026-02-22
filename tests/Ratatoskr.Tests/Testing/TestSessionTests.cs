using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;
using RatatoskrTestSessionContext = Ratatoskr.Testing.TestSessionContext;

namespace Ratatoskr.Tests.Testing;

public class TestSessionTests
{
    [Test]
    public async Task CreateSession_GeneratesUniqueSessionId()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr();

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        // Act
        await using var session1 = harness.CreateSession();
        await using var session2 = harness.CreateSession();

        // Assert
        session1.SessionId.Should().NotBeNullOrEmpty();
        session2.SessionId.Should().NotBeNullOrEmpty();
        session1.SessionId.Should().NotBe(session2.SessionId);
    }

    [Test]
    public async Task ParallelSessions_MessagesAreIsolated()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
        });

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();
        var bus = provider.GetRequiredService<IRatatoskr>();

        await using var session1 = harness.CreateSession();
        await using var session2 = harness.CreateSession();

        // Act - Publish in session1 context
        RatatoskrTestSessionContext.CurrentSessionId = session1.SessionId;
        await bus.PublishDirectAsync(new TestEvent { Id = "s1", Data = "session 1" });

        // Publish in session2 context
        RatatoskrTestSessionContext.CurrentSessionId = session2.SessionId;
        await bus.PublishDirectAsync(new TestEvent { Id = "s2", Data = "session 2" });

        RatatoskrTestSessionContext.CurrentSessionId = null;

        // Assert - Each session only sees its own messages
        session1.Sent.ShouldContain<TestEvent>(e => e.Id == "s1");
        session1.Sent.ShouldHaveCount(1);

        session2.Sent.ShouldContain<TestEvent>(e => e.Id == "s2");
        session2.Sent.ShouldHaveCount(1);

        // Global sink has all messages
        harness.Sent.Messages.Should().HaveCount(2);
    }

    [Test]
    public async Task CreateScope_SetsSessionContext_MessagesAreTagged()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
        });

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        await using var session = harness.CreateSession();

        // Act - Publish within a session scope
        using (var scope = session.CreateScope())
        {
            RatatoskrTestSessionContext.CurrentSessionId.Should().Be(session.SessionId);

            var bus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(new TestEvent { Id = "scoped", Data = "from scope" });
        }

        // Session context restored after scope disposal
        RatatoskrTestSessionContext.CurrentSessionId.Should().BeNull();

        // Assert
        session.Sent.ShouldContain<TestEvent>(e => e.Id == "scoped");
        session.Sent.ShouldHaveCount(1);
    }

    [Test]
    public async Task SimulateReceiveAsync_SetsSessionContext_HandlerSeesSessionId()
    {
        // Arrange
        var handler = new SessionCapturingHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
            bus.AddEventConsumeChannel("events-in", c => c.Consumes<TestEvent>());
            bus.AddHandler<TestEvent, SessionCapturingHandler>(handler);
        });

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        await using var session = harness.CreateSession();

        // Act
        await session.SimulateReceiveAsync(new TestEvent { Id = "sim", Data = "test" });

        // Assert - Handler should have seen the session ID
        handler.CapturedSessionId.Should().Be(session.SessionId);
    }

    [Test]
    public async Task SimulateReceiveAsync_DispatchesToHandlers()
    {
        // Arrange
        var handler = new TestEventHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
            bus.AddEventConsumeChannel("events-in", c => c.Consumes<TestEvent>());
            bus.AddHandler<TestEvent, TestEventHandler>(handler);
        });

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        await using var session = harness.CreateSession();

        // Act
        await session.SimulateReceiveAsync(new TestEvent { Id = "dispatch", Data = "test" });

        // Assert - Handler received the message
        handler.HandledMessages.Should().ContainSingle(m => m.Id == "dispatch");
    }

    [Test]
    public async Task Dispose_CancelsWaiters()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
        });

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        var session = harness.CreateSession();

        // Start a waiter
        var waitTask = session.Sent.WaitForAsync<TestEvent>(timeout: TimeSpan.FromSeconds(30));

        // Act - Dispose should cancel the waiter
        await session.DisposeAsync();

        // Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() => waitTask);
    }

    [Test]
    public async Task MessageSinkView_WaitForAsync_ReceivesFutureMessages()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
        });

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();
        var bus = provider.GetRequiredService<IRatatoskr>();

        await using var session = harness.CreateSession();

        // Act - Start waiting, then publish
        var waitTask = session.Sent.WaitForAsync<TestEvent>(
            e => e.Data == "delayed",
            timeout: TimeSpan.FromSeconds(5));

        // Publish with session context on a background task
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            RatatoskrTestSessionContext.CurrentSessionId = session.SessionId;
            await bus.PublishDirectAsync(new TestEvent { Id = "late", Data = "delayed" });
            RatatoskrTestSessionContext.CurrentSessionId = null;
        });

        var result = await waitTask;

        // Assert
        result.Should().NotBeNull();
        result.Message.Id.Should().Be("late");
    }

    [Test]
    public async Task Dispatcher_PropagatesSessionFromMessageHeaders()
    {
        // Arrange - This tests that when the MessageDispatcher receives a message
        // with a session header (e.g., from a real broker), it sets the AsyncLocal
        // so handlers see the session context.
        var handler = new SessionCapturingHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
            bus.AddEventConsumeChannel("events-in", c => c.Consumes<TestEvent>());
            bus.AddHandler<TestEvent, SessionCapturingHandler>(handler);
        });

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<MessageDispatcher>();
        var serializer = provider.GetRequiredService<IMessageSerializer>();

        // Create message with session header (simulating what a real broker consumer would provide)
        var testEvent = new TestEvent { Id = "broker-msg", Data = "from broker" };
        var body = serializer.Serialize(testEvent);
        var props = new MessageProperties
        {
            Type = "test.event",
            ContentType = serializer.ContentType
        };
        props.Headers[RatatoskrTestSessionContext.SessionHeaderName] = "test-session-abc";

        // Act - Dispatch directly (bypassing TestSession to simulate real broker path)
        RatatoskrTestSessionContext.CurrentSessionId = null; // Ensure no pre-existing session
        await dispatcher.DispatchAsync(body, props, CancellationToken.None);

        // Assert - Handler should have seen the session ID from the message headers
        handler.CapturedSessionId.Should().Be("test-session-abc");

        // Session context should be restored after dispatch
        RatatoskrTestSessionContext.CurrentSessionId.Should().BeNull();
    }

    [Test]
    public async Task RouteMessages_PublishedMessagesDispatchedToHandlers()
    {
        // Arrange - Enable RouteMessages so published messages are dispatched in-process
        var handler = new TestEventHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(
            bus =>
            {
                bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
                bus.AddEventConsumeChannel("events-in", c => c.Consumes<TestEvent>());
                bus.AddHandler<TestEvent, TestEventHandler>(handler);
            },
            transportOptions: o => o.RouteMessages = true);

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRatatoskr>();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        await using var session = harness.CreateSession();

        // Act - Publish within session context, message should also be dispatched to handler
        using (var scope = session.CreateScope())
        {
            var scopedBus = scope.ServiceProvider.GetRequiredService<IRatatoskr>();
            await scopedBus.PublishDirectAsync(new TestEvent { Id = "routed", Data = "should be handled" });
        }

        // Assert - Message was captured AND dispatched to handler
        session.Sent.ShouldContain<TestEvent>(e => e.Id == "routed");
        handler.HandledMessages.Should().ContainSingle(m => m.Id == "routed");
    }

    // Test handler that captures the session ID
    private class SessionCapturingHandler : IMessageHandler<TestEvent>
    {
        public string? CapturedSessionId { get; private set; }

        public Task HandleAsync(TestEvent message, MessageProperties properties, CancellationToken cancellationToken)
        {
            CapturedSessionId = RatatoskrTestSessionContext.CurrentSessionId;
            return Task.CompletedTask;
        }
    }
}
