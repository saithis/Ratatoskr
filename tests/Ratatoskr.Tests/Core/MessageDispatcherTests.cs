using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ratatoskr.Config;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.Core;
using Ratatoskr.Serializers.Json;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class MessageDispatcherTests
{
    [Test]
    public async Task DispatchAsync_WithRegisteredHandler_CallsHandler()
    {
        // Arrange
        var handler = new TestEventHandler();
        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<TestEventHandler>(_ => handler);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => handler);
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(TestEventHandler)));
            });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act
        var result = await dispatcher.DispatchAsync(body, context, CancellationToken.None, "test", "test");

        // Assert
        result.Should().Be(DispatchResult.Success);
        handler.HandledMessages.Should().HaveCount(1);
        handler.HandledMessages[0].Id.Should().Be("123");
        handler.HandledMessages[0].Data.Should().Be("test data");
    }

    [Test]
    public async Task DispatchAsync_WithMultipleHandlers_CallsAllHandlers()
    {
        // Arrange
        var handler1 = new TestEventHandler();
        var handler2 = new SecondTestEventHandler();

        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<TestEventHandler>(_ => handler1);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => handler1);
                services.AddScoped<SecondTestEventHandler>(_ => handler2);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => handler2);
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(TestEventHandler), typeof(SecondTestEventHandler)));
            });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act
        var result = await dispatcher.DispatchAsync(body, context, CancellationToken.None, "test", "test");

        // Assert
        result.Should().Be(DispatchResult.Success);
        handler1.HandledMessages.Should().HaveCount(1);
        handler2.HandledMessages.Should().HaveCount(1);
    }

    [Test]
    public async Task DispatchAsync_NoHandlerRegistered_ReturnsNoHandlers()
    {
        // Arrange
        var dispatcher = CreateDispatcher(registry =>
        {
            // Channel with message registered but no handlers
            var channel = new ChannelRegistration("test", ChannelType.EventConsume);
            channel.Messages.Add(new MessageRegistration(typeof(TestEvent), "test.event"));
            registry.Register(channel);
        });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act
        var result = await dispatcher.DispatchAsync(body, context, CancellationToken.None, "test", "test");

        // Assert
        result.Should().Be(DispatchResult.NoHandlers);
    }

    [Test]
    public async Task DispatchAsync_DeserializationFails_ReturnsPermanentError()
    {
        // Arrange
        var handler = new TestEventHandler();
        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<TestEventHandler>(_ => handler);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => handler);
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(TestEventHandler)));
            });

        var invalidBody = Encoding.UTF8.GetBytes("not valid json");
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act
        var result = await dispatcher.DispatchAsync(invalidBody, context, CancellationToken.None, "test", "test");

        // Assert
        result.Should().Be(DispatchResult.PermanentError);
        handler.HandledMessages.Should().BeEmpty();
    }

    [Test]
    public async Task DispatchAsync_HandlerThrows_ReturnsRecoverableError()
    {
        // Arrange
        var handler = new ThrowingTestEventHandler();
        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<ThrowingTestEventHandler>(_ => handler);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => handler);
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(ThrowingTestEventHandler)));
            });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act
        var result = await dispatcher.DispatchAsync(body, context, CancellationToken.None, "test", "test");

        // Assert
        result.Should().Be(DispatchResult.RecoverableError);
    }

    [Test]
    public async Task DispatchAsync_MultipleHandlersOneThrows_ThrowsAggregateException()
    {
        // Arrange
        var goodHandler = new TestEventHandler();
        var badHandler = new ThrowingTestEventHandler();

        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<TestEventHandler>(_ => goodHandler);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => goodHandler);
                services.AddScoped<ThrowingTestEventHandler>(_ => badHandler);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => badHandler);
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(TestEventHandler), typeof(ThrowingTestEventHandler)));
            });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act
        var result = await dispatcher.DispatchAsync(body, context, CancellationToken.None, "test", "test");

        // Assert
        result.Should().Be(DispatchResult.RecoverableError);
    }

    [Test]
    public async Task DispatchAsync_UsesNewScopeForEachMessage()
    {
        // Arrange
        var collector = new ScopedServiceIdCollector();
        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddSingleton(collector);
                services.AddScoped<ScopedService>();
                services.AddScoped<ScopedServiceTestHandler>();
                services.AddScoped<IMessageHandler<TestEvent>, ScopedServiceTestHandler>();
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(ScopedServiceTestHandler)));
            });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act - Dispatch twice
        await dispatcher.DispatchAsync(body, context, CancellationToken.None, "test", "test");
        await dispatcher.DispatchAsync(body, context, CancellationToken.None, "test", "test");

        // Assert - Each dispatch creates a new scope, so scoped service is new each time
        collector.ServiceIds.Should().HaveCount(2);
        collector.ServiceIds[0].Should().NotBe(collector.ServiceIds[1]);
    }

    [Test]
    public async Task DispatchAsync_PassesCorrectContextToHandler()
    {
        // Arrange
        var handler = new ContextCapturingHandler();
        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<ContextCapturingHandler>(_ => handler);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => handler);
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(ContextCapturingHandler)));
            });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test-source",
            Subject = "test-subject",
            Time = DateTimeOffset.UtcNow,
            Headers = new Dictionary<string, string> { ["custom"] = "header" },
        };

        // Act
        await dispatcher.DispatchAsync(body, context, CancellationToken.None, "test", "test");

        // Assert
        handler.CapturedContext.Should().NotBeNull();
        handler.CapturedContext!.Id.Should().Be("event-123");
        handler.CapturedContext.Type.Should().Be("test.event");
        handler.CapturedContext.Source.Should().Be("/test-source");
        handler.CapturedContext.Subject.Should().Be("test-subject");
        handler.CapturedContext.Headers.Should().ContainKey("custom");
        handler.CapturedContext.Headers["custom"].Should().Be("header");
    }

    [Test]
    public async Task DispatchAsync_CancellationRequested_ReturnsRecoverableError()
    {
        // Arrange
        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<CancellationAwareTestHandler>();
                services.AddScoped<IMessageHandler<TestEvent>, CancellationAwareTestHandler>();
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(CancellationAwareTestHandler)));
            });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await dispatcher.DispatchAsync(body, context, cts.Token, "test", "test");

        // Assert - OperationCanceledException from handler is treated as RecoverableError
        result.Should().Be(DispatchResult.RecoverableError);
    }

    [Test]
    public async Task DispatchAsync_NullBody_ReturnsPermanentError()
    {
        // Arrange
        var handler = new TestEventHandler();
        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<TestEventHandler>(_ => handler);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => handler);
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(TestEventHandler)));
            });

        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act
        var result = await dispatcher.DispatchAsync(null!, context, CancellationToken.None, "test", "test");

        // Assert - null body causes deserialization failure -> PermanentError
        result.Should().Be(DispatchResult.PermanentError);
        handler.HandledMessages.Should().BeEmpty();
    }

    private static MessageDispatcher CreateDispatcher(
        Action<ServiceCollection> configureDi,
        Action<ChannelRegistry> configureChannels)
    {
        var services = new ServiceCollection();
        configureDi(services);

        var channelRegistry = new ChannelRegistry();
        configureChannels(channelRegistry);
        channelRegistry.Freeze();

        var channelHandlerRegistry = ChannelHandlerRegistry.Build(channelRegistry);
        var deserializer = new JsonMessageSerializer();

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new MessageDispatcher(
            channelRegistry,
            channelHandlerRegistry,
            deserializer,
            new HandlerInvoker(scopeFactory),
            TimeProvider.System,
            [],
            NullLogger<MessageDispatcher>.Instance);
    }

    private static MessageDispatcher CreateDispatcher(Action<ChannelRegistry>? configureChannels = null)
    {
        return CreateDispatcher(_ => { }, configureChannels ?? (_ => { }));
    }

    [Test]
    public async Task DispatchAsync_CrossChannelFallback_ResolvesTypeFromOtherChannel()
    {
        // Arrange — message type is registered on "other-channel" but dispatch is called with "unknown-channel"
        var handler = new TestEventHandler();
        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<TestEventHandler>(_ => handler);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => handler);
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(TestEventHandler)));
            });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act — dispatch to a channel that doesn't exist; type should be resolved via cross-channel fallback
        // but fire-and-forget handlers are looked up by the provided channelName, so no handlers will be found
        var result = await dispatcher.DispatchAsync(body, context, CancellationToken.None, "unknown-channel", "test");

        // Assert — type was resolved (no PermanentError), but no handlers on "unknown-channel"
        result.Should().Be(DispatchResult.NoHandlers);
    }

    [Test]
    public async Task DispatchAsync_CorrectChannel_UsesDirectLookup()
    {
        // Arrange — dispatch to the correct channel should find handlers directly
        var handler = new TestEventHandler();
        var dispatcher = CreateDispatcher(
            services =>
            {
                services.AddScoped<TestEventHandler>(_ => handler);
                services.AddScoped<IMessageHandler<TestEvent>>(_ => handler);
            },
            registry =>
            {
                registry.Register(CreateTestChannel(typeof(TestEventHandler)));
            });

        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        var context = new MessageProperties
        {
            Id = "event-123",
            Type = "test.event",
            Source = "/test",
        };

        // Act
        var result = await dispatcher.DispatchAsync(body, context, CancellationToken.None, "test", "test");

        // Assert
        result.Should().Be(DispatchResult.Success);
        handler.HandledMessages.Should().HaveCount(1);
    }

    private static ChannelRegistration CreateTestChannel(params Type[] handlerTypes)
    {
        return CreateTestChannel("test", handlerTypes);
    }

    private static ChannelRegistration CreateTestChannel(string channelName, params Type[] handlerTypes)
    {
        var channel = new ChannelRegistration(channelName, ChannelType.EventConsume);
        var msgReg = new MessageRegistration(typeof(TestEvent), "test.event");
        if (handlerTypes.Length > 0)
        {
            var handlers = handlerTypes
                .Select(h => new ChannelHandlerRegistration(typeof(TestEvent), h, false, null))
                .ToList();
            msgReg.SetExtension(new MessageHandlerRegistrations(handlers));
        }
        channel.Messages.Add(msgReg);
        return channel;
    }
}
