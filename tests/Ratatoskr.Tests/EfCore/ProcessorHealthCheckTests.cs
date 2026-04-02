using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.EfCore;

public class ProcessorHealthCheckTests
{
    [Test]
    public void AddRatatoskrOutbox_DefaultConfiguration_RegistersHealthCheck()
    {
        // Arrange
        var services = new ServiceCollection();
        
        // Act
        services.AddHealthChecks().AddRatatoskrOutbox<TestDbContext>();
        
        // Assert
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        
        options.Registrations.Should().Contain(r => r.Name == "ratatoskr-outbox-TestDbContext");
        var reg = options.Registrations.First(r => r.Name == "ratatoskr-outbox-TestDbContext");
        reg.Tags.Should().Contain("ready");
    }

    [Test]
    public void AddRatatoskrInbox_DefaultConfiguration_RegistersHealthCheck()
    {
        // Arrange
        var services = new ServiceCollection();
        
        // Act
        services.AddHealthChecks().AddRatatoskrInbox<TestDbContext>();
        
        // Assert
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        
        options.Registrations.Should().Contain(r => r.Name == "ratatoskr-inbox-TestDbContext");
        var reg = options.Registrations.First(r => r.Name == "ratatoskr-inbox-TestDbContext");
        reg.Tags.Should().Contain("ready");
    }

    [Test]
    public async Task CheckHealthAsync_WhenLastSuccessIsRecent_ReturnsHealthy()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var processor = new TestProcessor(timeProvider);
        
        // Last success is exactly now.
        var healthCheck = new ProcessorHealthCheck<TestProcessor>(
            processor,
            timeProvider,
            TimeSpan.FromMinutes(2)
        );

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Test]
    public async Task CheckHealthAsync_WhenLastSuccessIsTooOld_ReturnsUnhealthy()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        
        // When processor is created, LastSuccessfulProcessingAt is initialized to timeProvider.GetUtcNow()
        var processor = new TestProcessor(timeProvider);
        
        var healthCheck = new ProcessorHealthCheck<TestProcessor>(
            processor,
            timeProvider,
            TimeSpan.FromMinutes(2)
        );

        // Advance time beyond the healthy threshold
        timeProvider.Advance(TimeSpan.FromMinutes(3));

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        // Assert
        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
    
    private class TestProcessor : PollingBackgroundService
    {
        public TestProcessor(TimeProvider timeProvider) 
            : base(default!, timeProvider, NullLogger.Instance)
        {
        }

        protected override string ProcessorName => "Test";
        protected override TimeSpan PollingInterval => TimeSpan.FromSeconds(1);
        protected override TimeSpan RestartDelay => TimeSpan.FromSeconds(1);
        protected override TimeSpan LockAcquireTimeout => TimeSpan.FromSeconds(1);
        protected override string LockName => "TestLock";

        protected override Task ProcessBatchesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
