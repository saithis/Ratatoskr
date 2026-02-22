using System.Text.Json;
using AwesomeAssertions;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.Core;
using Ratatoskr.Testing;
using TUnit.Core;

namespace Ratatoskr.Tests.Testing;

public class MessageSinkTests
{
    private static async Task SendAsync(MessageSink sink, byte[] content, MessageProperties props)
    {
        await ((IMessageSender)sink).SendAsync(content, props, CancellationToken.None);
    }

    [Test]
    public async Task SendAsync_CapturesMessage()
    {
        // Arrange
        var sink = new MessageSink();
        var content = "test message"u8.ToArray();
        var props = new MessageProperties { Type = "test.event" };

        // Act
        await SendAsync(sink, content, props);

        // Assert
        sink.Messages.Should().HaveCount(1);
        var message = sink.Messages.First();
        message.Content.Should().BeEquivalentTo(content);
        message.Properties.Type.Should().Be("test.event");
    }

    [Test]
    public async Task SendAsync_MultipleMessages_CapturesAll()
    {
        // Arrange
        var sink = new MessageSink();

        // Act
        await SendAsync(sink, "msg1"u8.ToArray(), new MessageProperties { Type = "type1" });
        await SendAsync(sink, "msg2"u8.ToArray(), new MessageProperties { Type = "type2" });
        await SendAsync(sink, "msg3"u8.ToArray(), new MessageProperties { Type = "type3" });

        // Assert - ConcurrentBag doesn't guarantee order, just verify all are captured
        sink.Messages.Should().HaveCount(3);
        var types = sink.Messages.Select(m => m.Properties.Type).OrderBy(t => t).ToList();
        types.Should().BeEquivalentTo(new[] { "type1", "type2", "type3" });
    }

    [Test]
    public async Task Clear_RemovesAllMessages()
    {
        // Arrange
        var sink = new MessageSink();
        await SendAsync(sink, "msg1"u8.ToArray(), new MessageProperties());
        await SendAsync(sink, "msg2"u8.ToArray(), new MessageProperties());
        sink.Messages.Should().HaveCount(2);

        // Act
        sink.Clear();

        // Assert
        sink.Messages.Should().BeEmpty();
    }

    [Test]
    public async Task Messages_IsThreadSafe()
    {
        // Arrange
        var sink = new MessageSink();
        IMessageSender sender = sink;
        var tasks = new List<Task>();

        // Act - Send messages concurrently
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var content = System.Text.Encoding.UTF8.GetBytes($"msg{index}");
                await sender.SendAsync(content, new MessageProperties { Type = $"type{index}" }, CancellationToken.None);
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - All messages should be captured without loss
        sink.Messages.Should().HaveCount(100);
    }

    [Test]
    public async Task SentMessage_Deserialize_ReturnsCorrectObject()
    {
        // Arrange
        var sink = new MessageSink();
        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var serialized = JsonSerializer.SerializeToUtf8Bytes(testEvent);

        await SendAsync(sink, serialized, new MessageProperties());

        // Act
        var message = sink.Messages.First();
        var deserialized = message.Deserialize<TestEvent>();

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be("123");
        deserialized.Data.Should().Be("test data");
    }

    [Test]
    public async Task SentMessage_ContentAsString_ReturnsUtf8String()
    {
        // Arrange
        var sink = new MessageSink();
        var content = "hello world"u8.ToArray();

        await SendAsync(sink, content, new MessageProperties());

        // Act
        var message = sink.Messages.First();
        var stringContent = message.ContentAsString;

        // Assert
        stringContent.Should().Be("hello world");
    }

    [Test]
    public async Task SentMessage_SentAt_RecordsTimestamp()
    {
        // Arrange
        var sink = new MessageSink();
        var beforeSend = DateTimeOffset.UtcNow;

        // Act
        await SendAsync(sink, "test"u8.ToArray(), new MessageProperties());

        var afterSend = DateTimeOffset.UtcNow;

        // Assert
        var message = sink.Messages.First();
        message.SentAt.Should().BeOnOrAfter(beforeSend);
        message.SentAt.Should().BeOnOrBefore(afterSend);
    }

    [Test]
    public async Task SendAsync_WithComplexProperties_CapturesAll()
    {
        // Arrange
        var sink = new MessageSink();
        var props = new MessageProperties
        {
            Type = "complex.event",
            Source = "/test-service",
            Subject = "test-subject",
            Id = "msg-123",
            Time = DateTimeOffset.UtcNow,
            ContentType = "application/json",
            Headers = { ["custom-header"] = "custom-value" },
            TransportMetadata = { ["tenant"] = "test-tenant" }
        };

        // Act
        await SendAsync(sink, "test"u8.ToArray(), props);

        // Assert
        var message = sink.Messages.First();
        message.Properties.Type.Should().Be("complex.event");
        message.Properties.Source.Should().Be("/test-service");
        message.Properties.Subject.Should().Be("test-subject");
        message.Properties.Id.Should().Be("msg-123");
        message.Properties.Headers.Should().ContainKey("custom-header");
        message.Properties.TransportMetadata.Should().ContainKey("tenant");
    }

    [Test]
    public async Task Count_ReturnsNumberOfMessages()
    {
        // Arrange
        var sink = new MessageSink();
        await SendAsync(sink, "msg1"u8.ToArray(), new MessageProperties());
        await SendAsync(sink, "msg2"u8.ToArray(), new MessageProperties());

        // Assert
        sink.Count.Should().Be(2);
    }

    [Test]
    public async Task WaitForAsync_WhenMessageAlreadyExists_ReturnsImmediately()
    {
        // Arrange
        var sink = new MessageSink();
        await SendAsync(sink, "msg"u8.ToArray(), new MessageProperties { Type = "test" });

        // Act
        var result = await sink.WaitForAsync(timeout: TimeSpan.FromMilliseconds(100));

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public async Task WaitForAsync_WhenMessageSentAfterWait_Returns()
    {
        // Arrange
        var sink = new MessageSink();

        // Act
        var waitTask = sink.WaitForAsync(timeout: TimeSpan.FromSeconds(2));

        await Task.Delay(20);
        await SendAsync(sink, "msg"u8.ToArray(), new MessageProperties { Type = "test" });

        var result = await waitTask;

        // Assert
        result.Should().NotBeNull();
    }

    [Test]
    public void WaitForAsync_WhenTimeout_ThrowsTimeoutException()
    {
        // Arrange
        var sink = new MessageSink();

        // Act
        Func<Task> act = async () => await sink.WaitForAsync(timeout: TimeSpan.FromMilliseconds(50));

        // Assert
        act.Should().ThrowAsync<TimeoutException>();
    }

    [Test]
    public async Task SendAsync_WithInnerSender_ForwardsToInner()
    {
        // Arrange
        var sink = new MessageSink();
        var innerMessages = new List<(byte[] Content, MessageProperties Props)>();
        var innerSender = new DelegatingMessageSender((content, props, ct) =>
        {
            innerMessages.Add((content, props));
            return Task.CompletedTask;
        });
        sink.SetInnerSender(innerSender);

        // Act
        await SendAsync(sink, "test"u8.ToArray(), new MessageProperties { Type = "test" });

        // Assert - captured by sink AND forwarded to inner
        sink.Messages.Should().HaveCount(1);
        innerMessages.Should().HaveCount(1);
    }

    private class DelegatingMessageSender(Func<byte[], MessageProperties, CancellationToken, Task> handler) : IMessageSender
    {
        public Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
            => handler(content, props, cancellationToken);
    }
}
