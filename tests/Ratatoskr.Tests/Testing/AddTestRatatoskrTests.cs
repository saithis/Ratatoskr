using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Testing;

public class AddTestRatatoskrTests
{
    [Test]
    public async Task AddTestRatatoskr_RegistersAllTestComponents()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
            bus.AddEventConsumeChannel("events-in", c => c.Consumes<TestEvent>());
        });

        await using var provider = services.BuildServiceProvider();

        // Assert - All components should be resolvable
        provider.GetRequiredService<IRatatoskr>().Should().NotBeNull();
        provider.GetRequiredService<MessageSink>().Should().NotBeNull();
        provider.GetRequiredService<RatatoskrTestHarness>().Should().NotBeNull();
        provider.GetRequiredService<IMessageSender>().Should().NotBeNull();
        provider.GetRequiredService<IMessagePropertiesEnricher>().Should().BeOfType<TestSessionEnricher>();
    }

    [Test]
    public async Task AddTestRatatoskr_WithoutConfigure_WorksWithDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr();

        await using var provider = services.BuildServiceProvider();

        // Assert
        var bus = provider.GetRequiredService<IRatatoskr>();
        bus.Should().NotBeNull();
    }

    [Test]
    public async Task AddTestRatatoskr_PublishAndVerify_EndToEnd()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
        });

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRatatoskr>();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        // Act
        await bus.PublishDirectAsync(new TestEvent { Id = "e2e", Data = "test" });

        // Assert
        harness.Sent.ShouldContain<TestEvent>(e => e.Id == "e2e");
        harness.Sent.ShouldHaveCount(1);
    }
}
