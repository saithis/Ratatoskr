using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.Testing;

/// <summary>
/// Extension methods for configuring Ratatoskr for testing.
/// </summary>
public static class TestingServiceCollectionExtensions
{
    /// <summary>
    /// Configures Ratatoskr with an in-memory transport for testing.
    /// This is the recommended way to set up Ratatoskr in unit tests.
    /// Combines <see cref="ServiceCollectionExtensions.AddRatatoskr"/> with <see cref="InMemoryRatatoskrExtensions.UseInMemory"/>.
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
        Action<RatatoskrBuilder>? configure = null)
    {
        services.AddRatatoskr(builder =>
        {
            builder.UseInMemory();
            configure?.Invoke(builder);
        });

        return services;
    }
}
