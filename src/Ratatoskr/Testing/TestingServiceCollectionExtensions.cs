using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.Testing;

/// <summary>
/// Extension methods for configuring Ratatoskr for testing.
/// </summary>
public static class TestingServiceCollectionExtensions
{
    /// <summary>
    /// Configures Ratatoskr with the test transport for testing.
    /// This is the recommended way to set up Ratatoskr in unit and integration tests
    /// without a <c>WebApplicationFactory</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// var services = new ServiceCollection();
    /// services.AddLogging();
    /// services.AddTestRatatoskr(bus =>
    /// {
    ///     bus.AddEventPublishChannel("events", c => c.Produces&lt;OrderCreated&gt;());
    ///     bus.AddEventConsumeChannel("events-in", c => c.Consumes&lt;OrderCreated&gt;());
    ///     bus.AddHandler&lt;OrderCreated, OrderCreatedHandler&gt;();
    /// });
    ///
    /// await using var provider = services.BuildServiceProvider();
    /// var harness = provider.GetRequiredService&lt;RatatoskrTestHarness&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddTestRatatoskr(
        this IServiceCollection services,
        Action<RatatoskrBuilder>? configure = null,
        Action<TestTransportOptions>? transportOptions = null)
    {
        var options = new TestTransportOptions();
        transportOptions?.Invoke(options);

        services.AddRatatoskr(builder =>
        {
            builder.UseTestTransport(options);
            configure?.Invoke(builder);
        });

        // Decorate enricher AFTER AddRatatoskr registers it
        services.DecorateEnricherWithSessionSupport();

        return services;
    }
}
