using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class InboxConfigurationValidatorTests
{
    [Test]
    public void Validate_NoInboxHandlers_DoesNotThrow()
    {
        // Arrange - channel with fire-and-forget handlers only
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>()));

        var channelRegistry = builder.ChannelRegistry;
        var handlerRegistry = ChannelHandlerRegistry.Build(channelRegistry);

        // Act & Assert
        var act = () => InboxConfigurationValidator.Validate(channelRegistry, handlerRegistry);
        act.Should().NotThrow();
    }

    [Test]
    public void Validate_InboxHandlerWithEmptyKey_ThrowsInvalidOperationException()
    {
        // Arrange - manually create a registration with empty inbox key (bypasses builder validation)
        var channel = new ChannelRegistration("test-channel", ChannelType.EventConsume);
        channel.SetExtension(new ChannelInboxConfig(typeof(TestDbContext)));

        var messageReg = new MessageRegistration(typeof(TestEvent), "test.event");
        channel.Messages.Add(messageReg);

        var handlers = new List<ChannelHandlerRegistration>
        {
            new(typeof(TestEvent), typeof(TestEventHandler), IsInbox: true, InboxKey: "")
        };
        messageReg.SetExtension(new MessageHandlerRegistrations(handlers));

        var channelRegistry = new ChannelRegistry();
        channelRegistry.Register(channel);
        var handlerRegistry = ChannelHandlerRegistry.Build(channelRegistry);

        // Act & Assert
        var act = () => InboxConfigurationValidator.Validate(channelRegistry, handlerRegistry);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty stable key*");
    }

    [Test]
    public void Validate_InboxHandlerOnChannelWithoutUseInbox_ThrowsInvalidOperationException()
    {
        // Arrange - channel with inbox handlers but WITHOUT ChannelInboxConfig extension
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("handler-key")));

        var channelRegistry = builder.ChannelRegistry;
        var handlerRegistry = ChannelHandlerRegistry.Build(channelRegistry);

        // Act & Assert
        var act = () => InboxConfigurationValidator.Validate(channelRegistry, handlerRegistry);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not have UseInbox*");
    }

    [Test]
    public void Validate_ValidConfiguration_DoesNotThrow()
    {
        // Arrange - channel with inbox handlers AND ChannelInboxConfig extension
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel("test-channel", c => c
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("handler-key")));

        var channelRegistry = builder.ChannelRegistry;

        // Set the ChannelInboxConfig on the channel (normally done by UseInbox<TDbContext>())
        var channel = channelRegistry.GetConsumeChannel("test-channel")!;
        channel.SetExtension(new ChannelInboxConfig(typeof(TestDbContext)));

        var handlerRegistry = ChannelHandlerRegistry.Build(channelRegistry);

        // Act & Assert
        var act = () => InboxConfigurationValidator.Validate(channelRegistry, handlerRegistry);
        act.Should().NotThrow();
    }

    [Test]
    public void UseInbox_TwoChannelsSameDbContext_SharedDurability_DoesNotThrow()
    {
        // Arrange - two channels sharing the same DbContext;
        // options are configured once via AddEfCoreDurability
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddRatatoskr(bus =>
        {
            bus.AddEventConsumeChannel("channel-a", c => c
                .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("key-a"))
                .UseInbox<TestDbContext>());
            bus.AddEventConsumeChannel("channel-b", c => c
                .Consumes<OrderCreatedEvent>(m => m.WithHandler<NoOpOrderCreatedHandler>("key-b"))
                .UseInbox<TestDbContext>());
            bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox(inbox => inbox.WithPollingInterval(TimeSpan.FromSeconds(5))));
        });

        act.Should().NotThrow();
    }
    [Test]
    public void UseInbox_WithoutAddEfCoreDurability_ThrowsAtStartup()
    {
        // Arrange - UseInbox<TDbContext>() on channel without AddEfCoreDurability
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddRatatoskr(bus =>
        {
            bus.AddEventConsumeChannel("test-channel", c => c
                .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("key"))
                .UseInbox<TestDbContext>());
            // Missing: bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddEfCoreDurability*UseInbox*");
    }

    [Test]
    public void AddEfCoreDurability_WithoutUseInboxOrUseOutbox_ThrowsImmediately()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddRatatoskr(bus =>
        {
            bus.AddEfCoreDurability<TestDbContext>(d => { });
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires at least UseInbox() or UseOutbox()*");
    }

    [Test]
    public void Validate_ChannelWithUseInbox_ButOnlyFireAndForgetHandlers_Throws()
    {
        // Arrange — channel has UseInbox configured but handler has no key (implicit fire-and-forget)
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel("test-channel", c => c
            .UseInbox<TestDbContext>()
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>()));

        var channelRegistry = builder.ChannelRegistry;
        var handlerRegistry = ChannelHandlerRegistry.Build(channelRegistry);

        // Act & Assert — all handlers on inbox channels must have a stable key
        var act = () => InboxConfigurationValidator.Validate(channelRegistry, handlerRegistry);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*registered without a stable key*");
    }

    [Test]
    public void Validate_MultipleChannelsDifferentDbContexts_DoesNotThrow()
    {
        // Arrange — two channels with different DbContext types, both valid
        var services = new ServiceCollection();
        var builder = new RatatoskrBuilder(services);
        builder.AddEventConsumeChannel("channel-a", c => c
            .UseInbox<TestDbContext>()
            .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("key-a")));
        builder.AddEventConsumeChannel("channel-b", c => c
            .UseInbox<SecondTestDbContext>()
            .Consumes<OrderCreatedEvent>(m => m.WithHandler<NoOpOrderCreatedHandler>("key-b")));

        var channelRegistry = builder.ChannelRegistry;
        var handlerRegistry = ChannelHandlerRegistry.Build(channelRegistry);

        // Act & Assert
        var act = () => InboxConfigurationValidator.Validate(channelRegistry, handlerRegistry);
        act.Should().NotThrow();
    }
}

file class NoOpOrderCreatedHandler : IMessageHandler<OrderCreatedEvent>
{
    public Task HandleAsync(OrderCreatedEvent message, MessageProperties context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
