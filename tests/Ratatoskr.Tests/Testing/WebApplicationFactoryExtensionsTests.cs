using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

        // Assert - TestTransport should replace the real sender
        var sender = provider.GetRequiredService<IMessageSender>();
        sender.Should().NotBeOfType<FakeProdMessageSender>();

        var sink = provider.GetRequiredService<MessageSink>();
        sink.Should().NotBeNull();

        // Test harness should be available
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();
        harness.Should().NotBeNull();

        // Session enricher should be decorated
        var enricher = provider.GetRequiredService<IMessagePropertiesEnricher>();
        enricher.Should().BeOfType<TestSessionEnricher>();
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

    [Test]
    public async Task CreateTestSession_ReturnsWebTestSession()
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
        await using var session = factory.CreateTestSession();

        // Assert
        session.Should().NotBeNull();
        session.SessionId.Should().NotBeNullOrEmpty();
        session.Sent.Should().NotBeNull();

        await factory.DisposeAsync();
    }

    [Test]
    public async Task CreateTestSession_CreateHttpClient_InjectsSessionHeader()
    {
        // Arrange
        string? capturedSessionHeader = null;

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
            .WithRatatoskrTestServices()
            .WithWebHostBuilder(builder =>
            {
                // Add a test endpoint that captures the session header
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupFilter>(new TestEndpointStartupFilter(context =>
                    {
                        capturedSessionHeader = context.Request.Headers["X-Ratatoskr-Session"].ToString();
                        return Results.Ok();
                    }));
                });
            });

        await using var session = factory.CreateTestSession();
        var client = session.CreateHttpClient();

        // Act
        await client.GetAsync("/test-endpoint");

        // Assert - The HTTP request should carry the session header
        capturedSessionHeader.Should().Be(session.SessionId);

        await factory.DisposeAsync();
    }

    [Test]
    public async Task SessionPropagation_HttpRequest_TagsPublishedMessages()
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
            .WithRatatoskrTestServices()
            .WithWebHostBuilder(builder =>
            {
                // Add a test endpoint that publishes a message
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupFilter>(new TestEndpointStartupFilter(async context =>
                    {
                        var bus = context.RequestServices.GetRequiredService<IRatatoskr>();
                        await bus.PublishDirectAsync(new TestEvent { Id = "http-pub", Data = "from endpoint" });
                        return Results.Ok();
                    }));
                });
            });

        await using var session1 = factory.CreateTestSession();
        await using var session2 = factory.CreateTestSession();

        var client1 = session1.CreateHttpClient();
        var client2 = session2.CreateHttpClient();

        // Act - Make requests through different session clients
        await client1.GetAsync("/test-endpoint");
        await client2.GetAsync("/test-endpoint");

        // Assert - Each session only sees its own message
        session1.Sent.ShouldHaveCount(1);
        session1.Sent.ShouldContain<TestEvent>(e => e.Id == "http-pub");

        session2.Sent.ShouldHaveCount(1);
        session2.Sent.ShouldContain<TestEvent>(e => e.Id == "http-pub");

        // Global sink has all messages
        var harness = factory.GetTestHarness();
        harness.Sent.Messages.Should().HaveCount(2);

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

    /// <summary>
    /// Startup filter that adds a test endpoint middleware at /test-endpoint.
    /// </summary>
    private class TestEndpointStartupFilter(Func<HttpContext, Task<IResult>> handler) : IStartupFilter
    {
        public TestEndpointStartupFilter(Func<HttpContext, IResult> syncHandler)
            : this(ctx => Task.FromResult(syncHandler(ctx))) { }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Path == "/test-endpoint")
                    {
                        var result = await handler(context);
                        await result.ExecuteAsync(context);
                        return;
                    }
                    await nextMiddleware();
                });
                next(app);
            };
        }
    }
}
