using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class ValidationTests
{
    [Test]
    public void Consumes_WithoutHandlers_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act & Assert
        var act = () => builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>((Action<MessageConsumptionBuilder<TestEvent>>)(m => { })));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Consumes<TestEvent>() requires at least one handler*");
    }

    [Test]
    public void Consumes_WithHandlers_DoesNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act & Assert
        var act = () => builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>()));

        act.Should().NotThrow();
    }

    [Test]
    public void Consumes_WithHandlersAndMessageConfig_DoesNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);

        // Act & Assert
        var act = () => builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(
                m => m.WithHandler<TestEventHandler>(),
                msg => msg.WithType("custom.type")));

        act.Should().NotThrow();
    }

    [Test]
    public void DuplicateInboxKey_ThrowsAtStartup()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert - AddRatatoskr calls ChannelHandlerRegistry.Build internally
        var act = () => services.AddRatatoskr(r => r
            .AddEventConsumeChannel("test-channel", c => c
                .Consumes<TestEvent>(m => m
                    .WithHandler<TestEventHandler>("same-key")
                    .WithHandler<SecondTestEventHandler>("same-key"))));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate inbox handler key*same-key*");
    }

    [Test]
    public void DuplicateInboxKey_AcrossChannels_ThrowsAtStartup()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddRatatoskr(r =>
        {
            r.AddEventConsumeChannel("channel-a", c => c
                .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("shared-key")));
            r.AddEventConsumeChannel("channel-b", c => c
                .Consumes<OrderCreatedEvent>(m => m.WithHandler<NoOpOrderCreatedHandler>("shared-key")));
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate inbox handler key*shared-key*");
    }

    [Test]
    public void WithoutInbox_OptOut_RegistersAsFireAndForget()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(m => m
                .WithHandler<TestEventHandler>("key-a", h => h.WithoutInbox())));

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert
        registry.GetFireAndForgetHandlers("test-channel", typeof(TestEvent)).Should().HaveCount(1);
        registry.GetInboxHandlers("test-channel").Should().BeEmpty();
        registry.HasNoInboxHandlers.Should().BeTrue();
    }

    [Test]
    public void WithHandler_WithStableKey_RegistersAsInbox()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("stable-key")));

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert
        var inboxHandlers = registry.GetInboxHandlers("test-channel");
        inboxHandlers.Should().HaveCount(1);
        inboxHandlers[0].IsInbox.Should().BeTrue();
        inboxHandlers[0].InboxKey.Should().Be("stable-key");
        registry.GetFireAndForgetHandlers("test-channel", typeof(TestEvent)).Should().BeEmpty();
    }

    [Test]
    public void WithHandler_WithoutKey_RegistersAsFireAndForget()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>()));

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert
        var fafHandlers = registry.GetFireAndForgetHandlers("test-channel", typeof(TestEvent));
        fafHandlers.Should().HaveCount(1);
        fafHandlers[0].IsInbox.Should().BeFalse();
        fafHandlers[0].InboxKey.Should().BeNull();
        registry.GetInboxHandlers("test-channel").Should().BeEmpty();
    }
}

/// <summary>
/// No-op handler for OrderCreatedEvent used in cross-channel tests.
/// </summary>
file class NoOpOrderCreatedHandler : IMessageHandler<OrderCreatedEvent>
{
    public Task HandleAsync(OrderCreatedEvent message, MessageProperties context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
