using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using TUnit.Core;

namespace Ratatoskr.Tests.Outbox;

public class OutboxMessageEntityTests
{
    [Test]
    public void Create_SetsRequiredProperties()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2025, 1, 24, 12, 0, 0, TimeSpan.Zero));
        var content = "test content"u8.ToArray();
        var props = new MessageProperties { Type = "test.event" };
        
        // Act
        var entity = OutboxMessageEntity.Create(content, props, fakeTime, "rabbitmq");
        
        // Assert
        entity.Id.Should().NotBe(Guid.Empty);
        entity.Content.Should().BeEquivalentTo(content);
        entity.SerializedProperties.Should().NotBeNull();
        entity.CreatedAt.Should().Be(fakeTime.GetUtcNow());
        entity.ProcessedAt.Should().BeNull();
        entity.ErrorCount.Should().Be(0);
        entity.IsPoisoned.Should().BeFalse();
    }

    [Test]
    public void MarkAsProcessing_SetsProcessingStartedAt()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");
        
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        
        // Act
        entity.MarkAsProcessing(fakeTime);
        
        // Assert
        entity.ProcessingStartedAt.Should().Be(fakeTime.GetUtcNow());
    }

    [Test]
    public void MarkAsProcessed_SetsProcessedAt()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");
        entity.MarkAsProcessing(fakeTime);
        
        fakeTime.Advance(TimeSpan.FromSeconds(1));
        
        // Act
        entity.MarkAsProcessed(fakeTime);
        
        // Assert
        entity.ProcessedAt.Should().Be(fakeTime.GetUtcNow());
        entity.ProcessingStartedAt.Should().BeNull(); // Cleared on completion
    }

    [Test]
    public void PublishFailed_IncreasesErrorCount()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");
        
        // Act
        entity.PublishFailed("Error 1", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));
        
        // Assert
        entity.ErrorCount.Should().Be(1);
        entity.Error.Should().Be("Error 1");
        entity.FailedAt.Should().NotBeNull();
        entity.ProcessingStartedAt.Should().BeNull(); // Cleared on failure
        entity.IsPoisoned.Should().BeFalse();
    }

    [Test]
    public void PublishFailed_CalculatesExponentialBackoffWithJitter()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2025, 1, 24, 12, 0, 0, TimeSpan.Zero));
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");

        // Act - First failure (base = 2^1 = 2s, jitter range = [1s, 2s))
        entity.PublishFailed("Error 1", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));

        // Assert — NextAttemptAt should be within the jitter range
        entity.NextAttemptAt.Should().NotBeNull();
        var now1 = fakeTime.GetUtcNow();
        entity.NextAttemptAt!.Value.Should().BeOnOrAfter(now1.AddSeconds(1));
        entity.NextAttemptAt!.Value.Should().BeOnOrBefore(now1.AddSeconds(2));

        // Act - Second failure (base = 2^2 = 4s, jitter range = [2s, 4s))
        fakeTime.Advance(TimeSpan.FromSeconds(3));
        entity.PublishFailed("Error 2", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));

        // Assert
        entity.ErrorCount.Should().Be(2);
        var now2 = fakeTime.GetUtcNow();
        entity.NextAttemptAt!.Value.Should().BeOnOrAfter(now2.AddSeconds(2));
        entity.NextAttemptAt!.Value.Should().BeOnOrBefore(now2.AddSeconds(4));

        // Act - Third failure (base = 2^3 = 8s, jitter range = [4s, 8s))
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        entity.PublishFailed("Error 3", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));

        // Assert
        entity.ErrorCount.Should().Be(3);
        var now3 = fakeTime.GetUtcNow();
        entity.NextAttemptAt!.Value.Should().BeOnOrAfter(now3.AddSeconds(4));
        entity.NextAttemptAt!.Value.Should().BeOnOrBefore(now3.AddSeconds(8));
    }

    [Test]
    public void PublishFailed_CapsBackoffAtMaxRetryDelay()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");
        var maxDelay = TimeSpan.FromSeconds(10);

        // Simulate many failures to hit the cap
        for (int i = 0; i < 10; i++)
        {
            entity.PublishFailed($"Error {i}", fakeTime, maxRetries: 20, maxDelay);
            fakeTime.Advance(TimeSpan.FromSeconds(1));
        }

        // Act - One more failure (would be 2^11 = 2048 seconds without cap)
        var beforeFail = fakeTime.GetUtcNow();
        entity.PublishFailed("Final error", fakeTime, maxRetries: 20, maxDelay);

        // Assert - Should be capped at maxDelay (10 seconds) with jitter: [5s, 10s)
        entity.NextAttemptAt.Should().NotBeNull();
        entity.NextAttemptAt!.Value.Should().BeOnOrAfter(beforeFail.AddSeconds(5));
        entity.NextAttemptAt!.Value.Should().BeOnOrBefore(beforeFail.AddSeconds(10));
    }

    [Test]
    public void PublishFailed_AfterMaxRetries_SetsPoisoned()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");
        var maxRetries = 3;
        
        // Act - Fail maxRetries times
        for (int i = 0; i < maxRetries; i++)
        {
            entity.PublishFailed($"Error {i}", fakeTime, maxRetries, TimeSpan.FromMinutes(5));
            fakeTime.Advance(TimeSpan.FromSeconds(1));
        }
        
        // Assert
        entity.ErrorCount.Should().Be((short)maxRetries);
        entity.IsPoisoned.Should().BeTrue();
        entity.NextAttemptAt.Should().BeNull(); // No more retries
    }

    [Test]
    public void MarkAsPoisoned_SetsCorrectState()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");
        
        // Act
        entity.MarkAsPoisoned("Manual poisoning", fakeTime);
        
        // Assert
        entity.IsPoisoned.Should().BeTrue();
        entity.Error.Should().Be("Manual poisoning");
        entity.FailedAt.Should().NotBeNull();
        entity.NextAttemptAt.Should().BeNull();
    }

    [Test]
    public void GetProperties_DeserializesCorrectly()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var props = new MessageProperties 
        { 
            Type = "test.event",
            Source = "/test",
            Subject = "test-subject"
        };
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), props, fakeTime, "rabbitmq");
        
        // Act
        var deserializedProps = entity.GetProperties();
        
        // Assert
        deserializedProps.Type.Should().Be("test.event");
        deserializedProps.Source.Should().Be("/test");
        deserializedProps.Subject.Should().Be("test-subject");
    }

    [Test]
    public void PublishFailed_TruncatesLongErrorMessages()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");
        var longError = new string('x', 3000); // Longer than 2000 char limit
        
        // Act
        entity.PublishFailed(longError, fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));
        
        // Assert
        entity.Error.Length.Should().Be(2000);
        entity.Error.Should().Be(longError[..2000]);
    }

    [Test]
    public void Create_NullContent_DoesNotThrow()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();

        // Act - passing null content (violates non-nullable contract but no runtime guard)
        var entity = OutboxMessageEntity.Create(null!, new MessageProperties(), fakeTime, "rabbitmq");

        // Assert - entity is created, content is null (no validation in Create)
        entity.Should().NotBeNull();
        entity.Id.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void GetProperties_CorruptedJson_ThrowsJsonException()
    {
        // Arrange - create entity then corrupt SerializedProperties via reflection
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");

        var backingField = typeof(BaseMessageEntity)
            .GetField("<SerializedProperties>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!;
        backingField.SetValue(entity, "not valid json");

        // Act
        var act = () => entity.GetProperties();

        // Assert
        act.Should().Throw<JsonException>();
    }

    [Test]
    public void GetProperties_NullDeserializationResult_ThrowsOutboxMessageSerializationException()
    {
        // Arrange - set SerializedProperties to JSON "null" which deserializes to null
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");

        var backingField = typeof(BaseMessageEntity)
            .GetField("<SerializedProperties>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!;
        backingField.SetValue(entity, "null");

        // Act
        var act = () => entity.GetProperties();

        // Assert
        act.Should().Throw<MessagePropertiesDeserializationException>();
    }

    [Test]
    public void MarkAsProcessingAndMarkAsProcessed_WhenCalled_IncrementVersion()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");
        entity.Version.Should().Be(0u);

        // Act & Assert - MarkAsProcessing increments
        entity.MarkAsProcessing(fakeTime);
        entity.Version.Should().Be(1u);

        // Act & Assert - MarkAsProcessed increments
        entity.MarkAsProcessed(fakeTime);
        entity.Version.Should().Be(2u);
    }

    [Test]
    public void PublishFailed_WhenCalled_IncrementVersion()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");

        // Act
        entity.PublishFailed("Error", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));

        // Assert
        entity.Version.Should().Be(1u);
    }

    [Test]
    public void MarkAsPoisoned_WhenCalled_IncrementVersion()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime, "rabbitmq");

        // Act
        entity.MarkAsPoisoned("reason", fakeTime);

        // Assert
        entity.Version.Should().Be(1u);
    }
}
