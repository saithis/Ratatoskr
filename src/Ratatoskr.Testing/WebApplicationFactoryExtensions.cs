using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Core;
using Ratatoskr.Testing;

namespace Ratatoskr.Testing;

/// <summary>
/// Extension methods for integrating Ratatoskr testing with <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
public static class WebApplicationFactoryExtensions
{
    /// <summary>
    /// Configures the <see cref="WebApplicationFactory{TEntryPoint}"/> to use Ratatoskr's test infrastructure.
    /// By default, replaces any configured message broker with an in-memory sink,
    /// removes broker-related hosted services, and registers the <see cref="RatatoskrTestHarness"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var factory = new WebApplicationFactory&lt;Program&gt;()
    ///     .WithRatatoskrTestServices();
    ///
    /// var harness = factory.GetTestHarness();
    /// var client = factory.CreateClient();
    ///
    /// await client.PostAsync("/api/orders", content);
    /// harness.Sent.ShouldContain&lt;OrderCreated&gt;();
    /// </code>
    /// </example>
    public static WebApplicationFactory<TEntryPoint> WithRatatoskrTestServices<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        Action<RatatoskrTestOptions>? configure = null)
        where TEntryPoint : class
    {
        var options = new RatatoskrTestOptions();
        configure?.Invoke(options);

        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.UseRatatoskrTestServices(options);
            });
        });
    }

    /// <summary>
    /// Gets the <see cref="RatatoskrTestHarness"/> from the factory's service provider.
    /// </summary>
    public static RatatoskrTestHarness GetTestHarness<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        return factory.Services.GetRequiredService<RatatoskrTestHarness>();
    }

    /// <summary>
    /// Replaces broker-related services with test equivalents.
    /// Use this in <c>ConfigureTestServices</c> when you need more control over the test host configuration.
    /// </summary>
    /// <example>
    /// <code>
    /// var factory = new WebApplicationFactory&lt;Program&gt;()
    ///     .WithWebHostBuilder(builder =>
    ///     {
    ///         builder.ConfigureTestServices(services =>
    ///         {
    ///             services.UseRatatoskrTestServices();
    ///             services.AddSingleton&lt;IMyService, FakeService&gt;();
    ///         });
    ///     });
    /// </code>
    /// </example>
    public static IServiceCollection UseRatatoskrTestServices(
        this IServiceCollection services,
        RatatoskrTestOptions? options = null)
    {
        options ??= new RatatoskrTestOptions();

        // Register the MessageSink (always needed for assertions)
        services.AddSingleton<MessageSink>(sp => new MessageSink
        {
            Registry = sp.GetService<ChannelRegistry>()
        });

        if (options.ReplaceTransport)
        {
            // Full in-memory mode: replace sender, remove broker services
            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(sp => sp.GetRequiredService<MessageSink>());

            // Replace the transport metadata enricher with a no-op
            services.RemoveAll<ITransportMessageMetadataEnricher>();
            services.AddSingleton<ITransportMessageMetadataEnricher, NoOpTransportMessageMetadataEnricher>();

            // Remove broker-related hosted services.
            // We filter by name to avoid coupling to RabbitMQ assembly types.
            services.RemoveAll<IHostedService>(descriptor =>
                descriptor.ImplementationType?.FullName?.Contains("RabbitMq") == true ||
                descriptor.ImplementationFactory?.Method.ReturnType.FullName?.Contains("RabbitMq") == true);
        }
        else
        {
            // Real transport mode: wrap the existing sender to capture messages
            var existingDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IMessageSender));

            if (existingDescriptor?.ImplementationType != null)
            {
                var originalType = existingDescriptor.ImplementationType;

                // Ensure the original sender type is registered so we can resolve it
                services.TryAddSingleton(originalType);

                // Replace IMessageSender with MessageSink that forwards to the original
                services.RemoveAll<IMessageSender>();
                services.AddSingleton<IMessageSender>(sp =>
                {
                    var sink = sp.GetRequiredService<MessageSink>();
                    var originalSender = (IMessageSender)sp.GetRequiredService(originalType);
                    sink.SetInnerSender(originalSender);
                    return sink;
                });
            }
            else
            {
                // Factory-based or instance-based registration - fall back to in-memory capture only
                services.RemoveAll<IMessageSender>();
                services.AddSingleton<IMessageSender>(sp => sp.GetRequiredService<MessageSink>());
            }
        }

        // Ensure MessageDispatcher is registered (needed for RatatoskrTestHarness)
        services.TryAddSingleton<MessageDispatcher>();

        // Register the test harness
        services.TryAddSingleton<RatatoskrTestHarness>();

        return services;
    }
}

internal static class ServiceCollectionRemoveExtensions
{
    /// <summary>
    /// Removes all service descriptors of the specified type that match a predicate.
    /// </summary>
    public static IServiceCollection RemoveAll<T>(
        this IServiceCollection services,
        Func<ServiceDescriptor, bool> predicate)
    {
        var serviceType = typeof(T);
        var descriptors = services
            .Where(d => d.ServiceType == serviceType && predicate(d))
            .ToList();

        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }

        return services;
    }
}

internal class NoOpTransportMessageMetadataEnricher : ITransportMessageMetadataEnricher
{
    public void Enrich(PublishInformation publishInformation, MessageProperties properties)
    {
        // No-op for test host
    }
}
