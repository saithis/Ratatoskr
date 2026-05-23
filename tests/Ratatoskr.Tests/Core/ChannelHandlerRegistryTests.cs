using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class ChannelHandlerRegistryTests
{
    [Test]
    public void Build_NoConsumeChannels_ReturnsEmptyRegistry()
    {
        // Arrange
        var channelRegistry = new ChannelRegistry();

        // Act
        var registry = ChannelHandlerRegistry.Build(channelRegistry);

        // Assert
        registry.HasNoInboxHandlers.Should().BeTrue();
        registry.GetAllInboxHandlers().Should().BeEmpty();
    }

    [Test]
    public void Build_FireAndForgetHandler_AvailableViaGetFireAndForgetHandlers()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert
        var handlers = registry.GetFireAndForgetHandlers("test-channel", typeof(TestEvent));
        handlers.Should().HaveCount(1);
        handlers[0].HandlerType.Should().Be(typeof(TestEventHandler));
        handlers[0].IsInbox.Should().BeFalse();
        handlers[0].InboxKey.Should().BeNull();
    }

    [Test]
    public void Build_InboxHandler_AvailableViaGetInboxHandlers()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("handler-key"))
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert
        var handlers = registry.GetInboxHandlers("test-channel");
        handlers.Should().HaveCount(1);
        handlers[0].HandlerType.Should().Be(typeof(TestEventHandler));
        handlers[0].IsInbox.Should().BeTrue();
        handlers[0].InboxKey.Should().Be("handler-key");
    }

    [Test]
    public void Build_InboxHandler_AvailableViaGetInboxRegistrationByKey()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("handler-key"))
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert
        var handler = registry.GetInboxRegistrationByKey("handler-key");
        handler.Should().NotBeNull();
        handler!.HandlerType.Should().Be(typeof(TestEventHandler));
        handler.InboxKey.Should().Be("handler-key");
    }

    [Test]
    public void Build_DuplicateInboxKey_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c =>
                c.Consumes<TestEvent>(m =>
                    m.WithHandler<TestEventHandler>("same-key")
                        .WithHandler<SecondTestEventHandler>("same-key")
                )
        );

        // Act & Assert
        var act = () => ChannelHandlerRegistry.Build(builder.ChannelRegistry);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Duplicate inbox handler key*same-key*");
    }

    [Test]
    public void GetFireAndForgetHandlers_UnknownChannel_ReturnsEmpty()
    {
        // Arrange
        var channelRegistry = new ChannelRegistry();
        var registry = ChannelHandlerRegistry.Build(channelRegistry);

        // Act
        var handlers = registry.GetFireAndForgetHandlers("nonexistent-channel", typeof(TestEvent));

        // Assert
        handlers.Should().BeEmpty();
    }

    [Test]
    public void GetInboxHandlers_UnknownChannel_ReturnsEmpty()
    {
        // Arrange
        var channelRegistry = new ChannelRegistry();
        var registry = ChannelHandlerRegistry.Build(channelRegistry);

        // Act
        var handlers = registry.GetInboxHandlers("nonexistent-channel");

        // Assert
        handlers.Should().BeEmpty();
    }

    [Test]
    public void GetInboxRegistrationByKey_UnknownKey_ReturnsNull()
    {
        // Arrange
        var channelRegistry = new ChannelRegistry();
        var registry = ChannelHandlerRegistry.Build(channelRegistry);

        // Act
        var handler = registry.GetInboxRegistrationByKey("nonexistent-key");

        // Assert
        handler.Should().BeNull();
    }

    [Test]
    public void HasNoInboxHandlers_WithNoInboxHandlers_ReturnsTrue()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert
        registry.HasNoInboxHandlers.Should().BeTrue();
    }

    [Test]
    public void HasNoInboxHandlers_WithInboxHandlers_ReturnsFalse()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("handler-key"))
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert
        registry.HasNoInboxHandlers.Should().BeFalse();
    }

    [Test]
    public void Build_MixedHandlers_CorrectlySeparated()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c =>
                c.Consumes<TestEvent>(m =>
                    m.WithHandler<TestEventHandler>()
                        .WithHandler<SecondTestEventHandler>("inbox-key")
                )
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert - fire-and-forget handler
        var fafHandlers = registry.GetFireAndForgetHandlers("test-channel", typeof(TestEvent));
        fafHandlers.Should().HaveCount(1);
        fafHandlers[0].HandlerType.Should().Be(typeof(TestEventHandler));

        // Assert - inbox handler
        var inboxHandlers = registry.GetInboxHandlers("test-channel");
        inboxHandlers.Should().HaveCount(1);
        inboxHandlers[0].HandlerType.Should().Be(typeof(SecondTestEventHandler));
        inboxHandlers[0].InboxKey.Should().Be("inbox-key");
    }

    [Test]
    public void Build_DuplicateInboxKey_AcrossChannels_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "channel-a",
            c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("shared-key"))
        );
        builder.AddEventConsumeChannel(
            "channel-b",
            c => c.Consumes<OrderCreatedEvent>(m => m.WithHandler<NoOpOrderHandler>("shared-key"))
        );

        // Act & Assert
        var act = () => ChannelHandlerRegistry.Build(builder.ChannelRegistry);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Duplicate inbox handler key*shared-key*");
    }

    [Test]
    public void GetAllInboxHandlers_ReturnsSnapshot_NotLiveCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("handler-key"))
        );

        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Act — get two separate references
        var first = registry.GetAllInboxHandlers();
        var second = registry.GetAllInboxHandlers();

        // Assert — should be equal in content but not the same reference (defensive copy)
        first.Should().NotBeSameAs(second);
        first.Should().BeEquivalentTo(second);
    }

    [Test]
    public void GetInboxHandlers_ByChannelAndMessageType_ReturnsCorrectSubset()
    {
        // Arrange — two message types on the same channel, each with inbox handlers
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c =>
                c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("key-test"))
                    .Consumes<OrderCreatedEvent>(m => m.WithHandler<NoOpOrderHandler>("key-order"))
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert — per-type lookup returns only the matching handler
        var testHandlers = registry.GetInboxHandlers("test-channel", typeof(TestEvent));
        testHandlers.Should().HaveCount(1);
        testHandlers[0].HandlerType.Should().Be(typeof(TestEventHandler));

        var orderHandlers = registry.GetInboxHandlers("test-channel", typeof(OrderCreatedEvent));
        orderHandlers.Should().HaveCount(1);
        orderHandlers[0].HandlerType.Should().Be(typeof(NoOpOrderHandler));
    }

    [Test]
    public void Build_LegacyKey_ResolvesViaGetInboxRegistrationByKey()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c =>
                c.Consumes<TestEvent>(m =>
                    m.WithHandler<TestEventHandler>("handler-v2", "handler-v1")
                )
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert — both primary and legacy key resolve to the same registration
        var byPrimary = registry.GetInboxRegistrationByKey("handler-v2");
        var byLegacy = registry.GetInboxRegistrationByKey("handler-v1");
        byPrimary.Should().NotBeNull();
        byLegacy.Should().NotBeNull();
        byLegacy.Should().BeSameAs(byPrimary);
    }

    [Test]
    public void Build_MultipleLegacyKeys_AllResolve()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c =>
                c.Consumes<TestEvent>(m =>
                    m.WithHandler<TestEventHandler>("handler-v3", "handler-v1", "handler-v2")
                )
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert
        var primary = registry.GetInboxRegistrationByKey("handler-v3");
        registry.GetInboxRegistrationByKey("handler-v1").Should().BeSameAs(primary);
        registry.GetInboxRegistrationByKey("handler-v2").Should().BeSameAs(primary);
    }

    [Test]
    public void Build_LegacyKeyConflictsWithPrimaryKey_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c =>
                c.Consumes<TestEvent>(m =>
                    m.WithHandler<TestEventHandler>("handler-a")
                        .WithHandler<SecondTestEventHandler>("handler-b", "handler-a")
                )
        );

        // Act & Assert
        var act = () => ChannelHandlerRegistry.Build(builder.ChannelRegistry);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Duplicate inbox handler key*handler-a*");
    }

    [Test]
    public void Build_LegacyKeyConflictsWithOtherLegacyKey_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c =>
                c.Consumes<TestEvent>(m =>
                    m.WithHandler<TestEventHandler>("handler-a", "shared-legacy")
                        .WithHandler<SecondTestEventHandler>("handler-b", "shared-legacy")
                )
        );

        // Act & Assert
        var act = () => ChannelHandlerRegistry.Build(builder.ChannelRegistry);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Duplicate inbox handler key*shared-legacy*");
    }

    [Test]
    public void Build_LegacyKeys_NotIncludedInGetAllInboxHandlers()
    {
        // Arrange — legacy keys should not cause duplicate registrations
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel(
            "test-channel",
            c =>
                c.Consumes<TestEvent>(m =>
                    m.WithHandler<TestEventHandler>("handler-v2", "handler-v1")
                )
        );

        // Act
        var registry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);

        // Assert — only one handler registration, not two
        registry.GetAllInboxHandlers().Should().HaveCount(1);
        registry.GetAllInboxHandlers().First().InboxKey.Should().Be("handler-v2");
    }
}

/// <summary>
/// No-op handler for OrderCreatedEvent used in cross-channel duplicate key tests.
/// </summary>
file class NoOpOrderHandler : IMessageHandler<OrderCreatedEvent>
{
    public Task HandleAsync(
        OrderCreatedEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;
}
