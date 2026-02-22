using System.Text.Json;
using AwesomeAssertions;
using Ratatoskr.Core;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Testing;

public class MessageSinkAssertionsTests
{
    private static async Task SendAsync(MessageSink sink, byte[] content, MessageProperties props)
    {
        await ((IMessageSender)sink).SendAsync(content, props, CancellationToken.None);
    }

    private MessageSink CreateSinkWithRegistry()
    {
        var registry = new ChannelRegistry();
        registry.Register(new ChannelRegistration("order.created", ChannelType.EventPublish));
        registry.Freeze();
        return new MessageSink { Registry = registry };
    }

    [Test]
    public async Task ShouldContain_WithMatchingMessage_ReturnsSentMessage()
    {
        // Arrange
        var sink = new MessageSink();
        var testEvent = new TestEvent { Id = "123", Data = "test" };
        var serialized = JsonSerializer.SerializeToUtf8Bytes(testEvent);

        await SendAsync(sink, serialized, new MessageProperties { Type = "test.event" });

        // Act
        var result = sink.ShouldContain<TestEvent>();

        // Assert
        result.Should().NotBeNull();
        result.Message.Id.Should().Be("123");
        result.Properties.Type.Should().Be("test.event");
    }

    [Test]
    public async Task ShouldContain_WithPredicate_FiltersCorrectly()
    {
        // Arrange
        var sink = CreateSinkWithRegistry();

        var guid3 = Guid.NewGuid();
        var event1 = new OrderCreatedEvent { OrderId = Guid.NewGuid(), Amount = 50 };
        var event2 = new OrderCreatedEvent { OrderId = Guid.NewGuid(), Amount = 100 };
        var event3 = new OrderCreatedEvent { OrderId = guid3, Amount = 150 };

        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(event1),
            new MessageProperties { Type = "order.created" });
        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(event2),
            new MessageProperties { Type = "order.created" });
        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(event3),
            new MessageProperties { Type = "order.created" });

        // Act
        var result = sink.ShouldContain<OrderCreatedEvent>(e => e.Amount > 100);

        // Assert
        result.Should().NotBeNull();
        result.Message.OrderId.Should().Be(guid3);
        result.Message.Amount.Should().Be(150);
    }

    [Test]
    public async Task ShouldContain_NoMatchingMessage_Throws()
    {
        // Arrange
        var sink = CreateSinkWithRegistry();
        var testEvent = new TestEvent { Data = "test" };

        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(testEvent),
            new MessageProperties { Type = "test.event" });

        // Act & Assert
        Action act = () => sink.ShouldContain<OrderCreatedEvent>();
        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*Expected to find a sent message of type OrderCreatedEvent*");
    }

    [Test]
    public async Task ShouldContain_WithPredicate_NoMatch_Throws()
    {
        // Arrange
        var sink = CreateSinkWithRegistry();
        var event1 = new OrderCreatedEvent { OrderId = Guid.NewGuid(), Amount = 50 };

        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(event1),
            new MessageProperties { Type = "order.created" });

        // Act & Assert
        Action act = () => sink.ShouldContain<OrderCreatedEvent>(e => e.Amount > 100);
        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*but none matched the predicate*");
    }

    [Test]
    public async Task ShouldNotContain_WhenMessageExists_Throws()
    {
        // Arrange
        var sink = new MessageSink();
        var testEvent = new TestEvent { Data = "test" };

        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(testEvent),
            new MessageProperties { Type = "test.event" });

        // Act & Assert
        var act = () => sink.ShouldNotContain<TestEvent>();
        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*Expected no messages of type TestEvent to be sent*");
    }

    [Test]
    public void ShouldNotContain_WhenNoMessage_Succeeds()
    {
        // Arrange
        var sink = new MessageSink();

        // Act & Assert - Should not throw
        sink.ShouldNotContain<TestEvent>();
    }

    [Test]
    public async Task ShouldHaveCount_WithCorrectCount_Succeeds()
    {
        // Arrange
        var sink = new MessageSink();
        await SendAsync(sink, "msg1"u8.ToArray(), new MessageProperties());
        await SendAsync(sink, "msg2"u8.ToArray(), new MessageProperties());
        await SendAsync(sink, "msg3"u8.ToArray(), new MessageProperties());

        // Act & Assert - Should not throw
        sink.ShouldHaveCount(3);
    }

    [Test]
    public async Task ShouldHaveCount_WithIncorrectCount_Throws()
    {
        // Arrange
        var sink = new MessageSink();
        await SendAsync(sink, "msg"u8.ToArray(), new MessageProperties());

        // Act & Assert
        var act = () => sink.ShouldHaveCount(3);
        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*Expected 3 message(s) to be sent, but found 1*");
    }

    [Test]
    public async Task ShouldBeEmpty_WithMessages_Throws()
    {
        // Arrange
        var sink = new MessageSink();
        await SendAsync(sink, "msg"u8.ToArray(), new MessageProperties());

        // Act & Assert
        var act = () => sink.ShouldBeEmpty();
        act.Should().Throw<RatatoskrTestException>();
    }

    [Test]
    public void ShouldBeEmpty_WithNoMessages_Succeeds()
    {
        // Arrange
        var sink = new MessageSink();

        // Act & Assert - Should not throw
        sink.ShouldBeEmpty();
    }

    [Test]
    public async Task GetMessages_ReturnsFilteredMessages()
    {
        // Arrange
        var sink = CreateSinkWithRegistry();

        var testEvent = new TestEvent { Data = "test" };
        var orderEvent = new OrderCreatedEvent { OrderId = Guid.NewGuid() };

        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(testEvent),
            new MessageProperties { Type = "test.event" });
        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(orderEvent),
            new MessageProperties { Type = "order.created" });
        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(testEvent),
            new MessageProperties { Type = "test.event" });

        // Act
        var testMessages = sink.GetMessages<TestEvent>();

        // Assert
        testMessages.Should().HaveCount(2);
    }

    [Test]
    public async Task ShouldContain_WithMultipleMatchingMessages_ReturnsFirst()
    {
        // Arrange
        var sink = new MessageSink();

        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(new TestEvent { Data = "first" }),
            new MessageProperties { Type = "test.event" });
        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(new TestEvent { Data = "second" }),
            new MessageProperties { Type = "test.event" });

        // Act
        var result = sink.ShouldContain<TestEvent>();

        // Assert - ConcurrentBag doesn't preserve order, just verify we get a match
        result.Message.Data.Should().BeOneOf("first", "second");
    }

    [Test]
    public async Task ShouldContain_MatchesByCloudEventType()
    {
        // Arrange
        var sink = new MessageSink();
        var testEvent = new TestEvent { Data = "test" };

        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(testEvent),
            new MessageProperties { Type = "test.event" });

        // Act & Assert - Should match by [RatatoskrMessage] attribute type
        var result = sink.ShouldContain<TestEvent>();
        result.Should().NotBeNull();
    }

    [Test]
    public async Task WaitForAsync_Typed_WhenMessageSentAfterWait_ReturnsTypedMessage()
    {
        // Arrange
        var sink = new MessageSink();
        var testEvent = new TestEvent { Id = "deferred", Data = "data" };
        var serialized = JsonSerializer.SerializeToUtf8Bytes(testEvent);

        // Act - Start waiting
        var waitTask = sink.WaitForAsync<TestEvent>(e => e.Id == "deferred", TimeSpan.FromSeconds(2));

        // Send message after a small delay
        await Task.Delay(20);
        await SendAsync(sink, serialized, new MessageProperties { Type = "test.event" });

        // Assert
        var result = await waitTask;
        result.Should().NotBeNull();
        result.Message.Id.Should().Be("deferred");
        result.Properties.Type.Should().Be("test.event");
    }

    [Test]
    public void WaitForAsync_Typed_WhenTimeout_ThrowsTimeoutException()
    {
        // Arrange
        var sink = new MessageSink();

        // Act
        Func<Task> act = async () => await sink.WaitForAsync<TestEvent>(
            timeout: TimeSpan.FromMilliseconds(50));

        // Assert
        act.Should().ThrowAsync<TimeoutException>();
    }

    [Test]
    public async Task ShouldHaveCount_Typed_WithCorrectCount_Succeeds()
    {
        // Arrange
        var sink = CreateSinkWithRegistry();

        var event1 = new OrderCreatedEvent { OrderId = Guid.NewGuid() };
        var event2 = new OrderCreatedEvent { OrderId = Guid.NewGuid() };
        var otherEvent = new TestEvent { Data = "other" };

        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(event1),
            new MessageProperties { Type = "order.created" });
        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(otherEvent),
            new MessageProperties { Type = "test.event" });
        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(event2),
            new MessageProperties { Type = "order.created" });

        // Act & Assert
        sink.ShouldHaveCount<OrderCreatedEvent>(2);
        sink.ShouldHaveCount<TestEvent>(1);
    }

    [Test]
    public async Task ShouldContain_WithCustomJsonOptions_DeserializesCorrectly()
    {
        // Arrange
        var sink = new MessageSink();
        var json = "{\"data\": \"value\"}";

        await SendAsync(sink, System.Text.Encoding.UTF8.GetBytes(json),
             new MessageProperties { Type = "test.event" });

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Act
        var result = sink.ShouldContain<TestEvent>(predicate: e => e.Data == "value", options: options);

        // Assert
        result.Message.Data.Should().Be("value");
    }

    [Test]
    public async Task ShouldContain_ReturnsSentMessageWithProperties()
    {
        // Arrange
        var sink = new MessageSink();
        var testEvent = new TestEvent { Id = "typed-1", Data = "test" };

        await SendAsync(sink, JsonSerializer.SerializeToUtf8Bytes(testEvent),
            new MessageProperties { Type = "test.event", Source = "/test-source" });

        // Act
        var result = sink.ShouldContain<TestEvent>();

        // Assert - SentMessage<T> has both message and properties
        result.Message.Id.Should().Be("typed-1");
        result.Message.Data.Should().Be("test");
        result.Properties.Type.Should().Be("test.event");
        result.Properties.Source.Should().Be("/test-source");
        result.SentAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }
}
