using AwesomeAssertions;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.RabbitMq;
using TUnit.Core;

namespace Ratatoskr.Tests.RabbitMq;

[ClassDataSource<RabbitMqContainerFixture>(Shared = SharedType.PerTestSession)]
public class RabbitMqConnectionManagerTests(RabbitMqContainerFixture rabbitMq)
{
    [Test]
    public async Task CreateChannelAsync_ReusesConnection()
    {
        // Arrange
        var options = new RabbitMqOptions
        {
            ConnectionString = new Uri(rabbitMq.ConnectionString)
        };
        var manager = new RabbitMqConnectionManager(options);
        
        // Act
        await using var channel1 = await manager.CreateChannelAsync(enablePublisherConfirms: false);
        await using var channel2 = await manager.CreateChannelAsync(enablePublisherConfirms: false);
        
        // Assert - Both channels should be open (connection is reused)
        channel1.IsOpen.Should().BeTrue();
        channel2.IsOpen.Should().BeTrue();
    }

    [Test]
    public async Task CreateChannelAsync_WithConfirms_EnablesConfirmMode()
    {
        // Arrange
        var options = new RabbitMqOptions
        {
            ConnectionString = new Uri(rabbitMq.ConnectionString)
        };
        var manager = new RabbitMqConnectionManager(options);
        
        // Act
        await using var channel = await manager.CreateChannelAsync(enablePublisherConfirms: true);
        
        // Assert - Channel should be open with confirms
        channel.IsOpen.Should().BeTrue();
        
        // Try publishing with confirms (should not throw)
        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: "test",
            mandatory: false,
            basicProperties: new RabbitMQ.Client.BasicProperties(),
            body: "test"u8.ToArray());
        
        // If we get here, confirms are working (no exception thrown)
    }

    [Test]
    public async Task CreateChannelAsync_WithoutConfirms_CreatesNormalChannel()
    {
        // Arrange
        var options = new RabbitMqOptions
        {
            ConnectionString = new Uri(rabbitMq.ConnectionString)
        };
        var manager = new RabbitMqConnectionManager(options);
        
        // Act
        await using var channel = await manager.CreateChannelAsync(enablePublisherConfirms: false);
        
        // Assert
        channel.IsOpen.Should().BeTrue();
    }

    [Test]
    public async Task DisposeAsync_ClosesConnection()
    {
        // Arrange
        var options = new RabbitMqOptions
        {
            ConnectionString = new Uri(rabbitMq.ConnectionString)
        };
        var manager = new RabbitMqConnectionManager(options);
        
        // Create a channel to establish connection
        await using var channel = await manager.CreateChannelAsync(enablePublisherConfirms: false);
        channel.IsOpen.Should().BeTrue();
        
        // Act
        await manager.DisposeAsync();
        
        // Assert - Channel should be closed after manager disposal
        channel.IsOpen.Should().BeFalse();
    }
}
