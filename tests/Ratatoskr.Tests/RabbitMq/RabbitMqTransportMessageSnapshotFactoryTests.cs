using System.Text;
using AwesomeAssertions;
using RabbitMQ.Client;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Tests.RabbitMq;

public class RabbitMqTransportMessageSnapshotFactoryTests
{
    [Test]
    public void FromBasicProperties_ValidUtf8Header_DecodesToString()
    {
        // Arrange
        var basicProps = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["my-header"] = Encoding.UTF8.GetBytes("hello world")
            }
        };

        // Act
        var result = RabbitMqTransportMessageSnapshotFactory.FromBasicProperties(basicProps, [], "", "");

        // Assert
        result.Headers["my-header"].Should().BeOfType<string>().And.Be("hello world");
    }

    [Test]
    public void FromBasicProperties_NonUtf8BinaryHeader_ReturnsByteArray()
    {
        // Arrange - bytes that are not valid UTF-8 (0xFF is never valid in UTF-8)
        var binaryData = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x80 };
        var basicProps = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["binary-header"] = binaryData
            }
        };

        // Act
        var result = RabbitMqTransportMessageSnapshotFactory.FromBasicProperties(basicProps, [], "", "");

        // Assert
        result.Headers["binary-header"].Should().BeOfType<byte[]>().Which.Should().BeEquivalentTo(binaryData);
    }

    [Test]
    public void FromBasicProperties_EmptyByteArrayHeader_DecodesToEmptyString()
    {
        // Arrange
        var basicProps = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["empty-header"] = Array.Empty<byte>()
            }
        };

        // Act
        var result = RabbitMqTransportMessageSnapshotFactory.FromBasicProperties(basicProps, [], "", "");

        // Assert
        result.Headers["empty-header"].Should().BeOfType<string>().And.Be("");
    }

    [Test]
    public void FromBasicProperties_NullHeaders_ReturnsEmptyHeaders()
    {
        // Arrange
        var basicProps = new BasicProperties { Headers = null };

        // Act
        var result = RabbitMqTransportMessageSnapshotFactory.FromBasicProperties(basicProps, [], "", "");

        // Assert - only standard AMQP properties may be present, no custom headers
        result.Headers.Should().NotBeNull();
        result.Headers.Should().NotContainKey("some-custom-header");
    }

    [Test]
    public void FromBasicProperties_NonByteTypedHeaders_PreservedAndNormalized()
    {
        // Arrange - native RabbitMQ-typed header values (long, bool, string)
        var basicProps = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["long-header"] = 42L,
                ["bool-header"] = true,
                ["string-header"] = "plain-string"
            }
        };

        // Act
        var result = RabbitMqTransportMessageSnapshotFactory.FromBasicProperties(basicProps, [], "", "");

        // Assert - non-byte[] values pass through NormalizeValue unchanged
        result.Headers["long-header"].Should().BeOfType<long>().Which.Should().Be(42L);
        result.Headers["bool-header"].Should().BeOfType<bool>().Which.Should().BeTrue();
        result.Headers["string-header"].Should().BeOfType<string>().Which.Should().Be("plain-string");
    }
}
