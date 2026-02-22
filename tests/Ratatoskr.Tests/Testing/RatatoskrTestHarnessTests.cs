using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.Testing;
using TUnit.Core;
using AwesomeAssertions;

namespace Ratatoskr.Tests.Testing;

public class RatatoskrTestHarnessTests
{
    [Test]
    public async Task SimulateReceiveAsync_ShouldDispatchMessage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<MessageTracker>();
        services.AddTestRatatoskr(builder =>
        {
            builder.AddHandler<TestMessage, TestHandler>();
            builder.AddEventConsumeChannel("test-in", c => c.Consumes<TestMessage>());
        });

        await using var provider = services.BuildServiceProvider();

        var harness = provider.GetRequiredService<RatatoskrTestHarness>();
        var tracker = provider.GetRequiredService<MessageTracker>();

        var message = new TestMessage { Content = "Test" };
        var result = await harness.SimulateReceiveAsync(message);

        result.Should().Be(DispatchResult.Success);
        tracker.ReceivedMessages.Should().ContainSingle()
            .Which.Content.Should().Be("Test");
    }

    [Test]
    public async Task SimulateReceiveAsync_NoHandlers_ThrowsRatatoskrTestException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(builder =>
        {
            // Register consume channel but NO handler
            builder.AddEventConsumeChannel("test-in", c => c.Consumes<TestMessage>());
        });

        await using var provider = services.BuildServiceProvider();

        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        var act = () => harness.SimulateReceiveAsync(new TestMessage { Content = "Test" });

        await act.Should().ThrowAsync<RatatoskrTestException>()
            .WithMessage("*No handlers found*TestMessage*");
    }

    [Test]
    public async Task SimulateReceiveAsync_NoConsumeChannel_ThrowsRatatoskrTestException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr();

        await using var provider = services.BuildServiceProvider();

        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        var act = () => harness.SimulateReceiveAsync(new TestMessage { Content = "Test" });

        await act.Should().ThrowAsync<RatatoskrTestException>()
            .WithMessage("*No handlers found*");
    }

    [Test]
    public async Task Sent_ShouldVerifyMessageSending()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(builder =>
        {
            builder.AddEventPublishChannel("test-out", c => c.Produces<TestMessage>());
        });

        await using var provider = services.BuildServiceProvider();

        var harness = provider.GetRequiredService<RatatoskrTestHarness>();
        var ratatoskr = provider.GetRequiredService<IRatatoskr>();

        await ratatoskr.PublishDirectAsync(new TestMessage { Content = "Hello" });

        harness.Sent.ShouldContain<TestMessage>(m => m.Content == "Hello");
    }

    [Test]
    public async Task Reset_ClearsAllCapturedMessages()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(builder =>
        {
            builder.AddEventPublishChannel("test-out", c => c.Produces<TestMessage>());
        });

        await using var provider = services.BuildServiceProvider();

        var harness = provider.GetRequiredService<RatatoskrTestHarness>();
        var ratatoskr = provider.GetRequiredService<IRatatoskr>();

        await ratatoskr.PublishDirectAsync(new TestMessage { Content = "msg1" });
        harness.Sent.Count.Should().Be(1);

        // Act
        harness.Reset();

        // Assert
        harness.Sent.Count.Should().Be(0);
        harness.Sent.Messages.Should().BeEmpty();
    }

    public class MessageTracker
    {
        public List<TestMessage> ReceivedMessages { get; } = new();
    }

    public class TestHandler(MessageTracker tracker) : IMessageHandler<TestMessage>
    {
        public Task HandleAsync(TestMessage message, MessageProperties properties, CancellationToken cancellationToken)
        {
            tracker.ReceivedMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    [RatatoskrMessage("test-message")]
    public class TestMessage
    {
        public string? Id { get; set; }
        public string? Content { get; set; }
    }
}
