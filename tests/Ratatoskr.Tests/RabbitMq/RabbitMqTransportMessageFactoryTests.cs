using System.Text;
using AwesomeAssertions;
using RabbitMQ.Client;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Tests.RabbitMq;

public class RabbitMqTransportMessageFactoryTests
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
        var result = RabbitMqTransportMessageFactory.FromBasicProperties(basicProps, [], "", "");

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
        var result = RabbitMqTransportMessageFactory.FromBasicProperties(basicProps, [], "", "");

        // Assert
        result.Headers["binary-header"].Should().BeOfType<byte[]>().And.BeEquivalentTo(binaryData);
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
        var result = RabbitMqTransportMessageFactory.FromBasicProperties(basicProps, [], "", "");

        // Assert
        result.Headers["empty-header"].Should().BeOfType<string>().And.Be("");
    }
}
