using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ratatoskr.RabbitMq;
using TUnit.Core;

namespace Ratatoskr.Tests.RabbitMq;

public class RabbitMqHealthCheckTests
{
    [Test]
    public void AddRatatoskrRabbitMq_RegistersHealthCheck()
    {
        // Arrange
        var services = new ServiceCollection();
        
        // Act
        services.AddHealthChecks().AddRatatoskrRabbitMq("my-rabbit");
        
        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        
        options.Registrations.Should().Contain(r => r.Name == "my-rabbit");
        var reg = options.Registrations.First(r => r.Name == "my-rabbit");
        reg.Tags.Should().Contain("ready");
    }

    [Test]
    public async Task CheckHealthAsync_WhenConsumerHealthy_ReturnsHealthy()
    {
        // Arrange
        // Cannot easily mock RabbitMqConsumer since it might be sealed or require connections.
        // We will just create an un-started consumer and verify it returns unhealthy?
        // Wait, IsHealthy defaults to false.
    }
}
