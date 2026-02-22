using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Testing;

public class WebApplicationFactoryExtensionsTests
{
    [Test]
    public async Task UseRatatoskrTestServices_ReplacesMessageSenderWithInMemory()
    {
        // Arrange - Build a service collection that mimics a real app's registration
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
        });
        // Simulate having a real sender registered
        services.AddSingleton<IMessageSender, FakeProdMessageSender>();
        services.AddSingleton<ITransportMessageMetadataEnricher, FakeProdEnricher>();

        // Act - Apply test services on top
        services.UseRatatoskrTestServices();

        await using var provider = services.BuildServiceProvider();

        // Assert - MessageSink should be registered
        var sender = provider.GetRequiredService<IMessageSender>();
        sender.Should().BeOfType<MessageSink>();

        var sink = provider.GetRequiredService<MessageSink>();
        sink.Should().NotBeNull();

        // Test harness should be available
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();
        harness.Should().NotBeNull();
    }

    [Test]
    public async Task UseRatatoskrTestServices_AllowsPublishingAndAssertions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRatatoskr(bus =>
        {
            bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
        });
        services.UseRatatoskrTestServices();

        await using var provider = services.BuildServiceProvider();

        var bus = provider.GetRequiredService<IRatatoskr>();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        // Act
        await bus.PublishDirectAsync(new TestEvent { Id = "waf-1", Data = "factory test" });

        // Assert
        harness.Sent.ShouldContain<TestEvent>(e => e.Data == "factory test");
    }

    [Test]
    public async Task WithRatatoskrTestServices_ReplacesServicesInWebApplicationFactory()
    {
        // Arrange
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddRatatoskr(bus =>
                    {
                        bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
                    });
                });
            })
            .WithRatatoskrTestServices();

        // Act
        var sink = factory.Services.GetRequiredService<MessageSink>();
        var harness = factory.GetTestHarness();

        // Assert
        sink.Should().NotBeNull();
        harness.Should().NotBeNull();
        harness.Sent.Should().BeSameAs(sink);

        await factory.DisposeAsync();
    }

    [Test]
    public async Task GetTestHarness_ReturnsHarnessFromFactory()
    {
        // Arrange
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddRatatoskr(bus =>
                    {
                        bus.AddEventPublishChannel("events", c => c.Produces<TestEvent>());
                    });
                });
            })
            .WithRatatoskrTestServices();

        // Act
        var harness = factory.GetTestHarness();

        // Assert
        harness.Should().NotBeNull();
        harness.Sent.Should().NotBeNull();

        await factory.DisposeAsync();
    }

    // Fake production services for testing replacement
    private class FakeProdMessageSender : IMessageSender
    {
        public Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Should have been replaced by test services");
    }

    private class FakeProdEnricher : ITransportMessageMetadataEnricher
    {
        public void Enrich(PublishInformation publishInformation, MessageProperties properties)
            => throw new InvalidOperationException("Should have been replaced by test services");
    }
}
