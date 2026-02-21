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
        var entity = OutboxMessageEntity.Create(content, props, fakeTime);
        
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
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);
        
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
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);
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
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);
        
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
    public void PublishFailed_CalculatesExponentialBackoff()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2025, 1, 24, 12, 0, 0, TimeSpan.Zero));
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);
        
        // Act - First failure (2^1 = 2 seconds)
        entity.PublishFailed("Error 1", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));
        
        // Assert
        entity.NextAttemptAt.Should().NotBeNull();
        entity.NextAttemptAt!.Value.Should().Be(fakeTime.GetUtcNow().AddSeconds(2));
        
        // Act - Second failure (2^2 = 4 seconds)
        fakeTime.Advance(TimeSpan.FromSeconds(3));
        entity.PublishFailed("Error 2", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));
        
        // Assert
        entity.ErrorCount.Should().Be(2);
        entity.NextAttemptAt!.Value.Should().Be(fakeTime.GetUtcNow().AddSeconds(4));
        
        // Act - Third failure (2^3 = 8 seconds)
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        entity.PublishFailed("Error 3", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));
        
        // Assert
        entity.ErrorCount.Should().Be(3);
        entity.NextAttemptAt!.Value.Should().Be(fakeTime.GetUtcNow().AddSeconds(8));
    }

    [Test]
    public void PublishFailed_CapsBackoffAtMaxRetryDelay()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);
        var maxDelay = TimeSpan.FromSeconds(10);
        
        // Simulate many failures to hit the cap
        for (int i = 0; i < 10; i++)
        {
            entity.PublishFailed($"Error {i}", fakeTime, maxRetries: 20, maxDelay);
            fakeTime.Advance(TimeSpan.FromSeconds(1));
        }
        
        // Act - One more failure (would be 2^10 = 1024 seconds without cap)
        var beforeFail = fakeTime.GetUtcNow();
        entity.PublishFailed("Final error", fakeTime, maxRetries: 20, maxDelay);
        
        // Assert - Should be capped at maxDelay (10 seconds)
        entity.NextAttemptAt.Should().NotBeNull();
        entity.NextAttemptAt!.Value.Should().Be(beforeFail.AddSeconds(10));
    }

    [Test]
    public void PublishFailed_AfterMaxRetries_SetsPoisoned()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);
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
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);
        
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
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), props, fakeTime);
        
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
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);
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
        var entity = OutboxMessageEntity.Create(null!, new MessageProperties(), fakeTime);

        // Assert - entity is created, content is null (no validation in Create)
        entity.Should().NotBeNull();
        entity.Id.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void GetProperties_CorruptedJson_ThrowsJsonException()
    {
        // Arrange - create entity then corrupt SerializedProperties via reflection
        var fakeTime = new FakeTimeProvider();
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);

        var backingField = typeof(OutboxMessageEntity)
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
        var entity = OutboxMessageEntity.Create("test"u8.ToArray(), new MessageProperties(), fakeTime);

        var backingField = typeof(OutboxMessageEntity)
            .GetField("<SerializedProperties>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!;
        backingField.SetValue(entity, "null");

        // Act
        var act = () => entity.GetProperties();

        // Assert
        act.Should().Throw<OutboxMessageSerializationException>();
    }
}
