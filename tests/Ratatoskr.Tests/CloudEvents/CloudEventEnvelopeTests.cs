using System.Text.Json;
using AwesomeAssertions;
using Ratatoskr.CloudEvents;

namespace Ratatoskr.Tests.CloudEvents;

public class CloudEventEnvelopeTests
{
    [Test]
    public void TryGetExtension_ReturnsTrue_WhenKeyExistsAndTypeMatches()
    {
        // Arrange
        var envelope = new CloudEventEnvelope
        {
            Id = "123",
            Source = "/test",
            Type = "test.type",
            Extensions = new Dictionary<string, object>
            {
                { "traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01" },
            },
        };

        // Act
        var result = envelope.TryGetExtension<string>("traceparent", out var value);

        // Assert
        result.Should().BeTrue();
        value.Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    }

    [Test]
    public void TryGetExtension_ReturnsFalse_WhenKeyDoesNotExist()
    {
        // Arrange
        var envelope = new CloudEventEnvelope
        {
            Id = "123",
            Source = "/test",
            Type = "test.type",
            Extensions = new Dictionary<string, object> { { "traceparent", "some-value" } },
        };

        // Act
        var result = envelope.TryGetExtension<string>("nonexistent", out var value);

        // Assert
        result.Should().BeFalse();
        value.Should().BeNull();
    }

    [Test]
    public void TryGetExtension_ReturnsFalse_WhenExtensionsIsNull()
    {
        // Arrange
        var envelope = new CloudEventEnvelope
        {
            Id = "123",
            Source = "/test",
            Type = "test.type",
            Extensions = null,
        };

        // Act
        var result = envelope.TryGetExtension<string>("traceparent", out var value);

        // Assert
        result.Should().BeFalse();
        value.Should().BeNull();
    }

    [Test]
    public void TryGetExtension_ReturnsTrue_WhenValueIsJsonElementAndCanDeserialize()
    {
        // Arrange
        var json = JsonSerializer.SerializeToElement(
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        );
        var envelope = new CloudEventEnvelope
        {
            Id = "123",
            Source = "/test",
            Type = "test.type",
            Extensions = new Dictionary<string, object> { { "traceparent", json } },
        };

        // Act
        var result = envelope.TryGetExtension<string>("traceparent", out var value);

        // Assert
        result.Should().BeTrue();
        value.Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    }

    [Test]
    public void TryGetExtension_ReturnsFalse_WhenValueIsJsonElementButCannotDeserialize()
    {
        // Arrange
        // Current value is a number, try to get as string (JsonElement.Deserialize<string> might throw or handle it depending on options, but usually strictly matching types)
        // Actually, let's try to get a complex object from a simple string JsonElement to force a mismatch or error that TryGetExtension catches
        var json = JsonSerializer.SerializeToElement("simple string");

        var envelope = new CloudEventEnvelope
        {
            Id = "123",
            Source = "/test",
            Type = "test.type",
            Extensions = new Dictionary<string, object> { { "traceparent", json } },
        };

        // Act
        // Intentionally wrong type
        var result = envelope.TryGetExtension<int>("traceparent", out var value);

        // Assert
        result.Should().BeFalse();
        value.Should().Be(default);
    }
}
