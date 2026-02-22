using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    /// By default, replaces any configured message broker with the test transport,
    /// removes broker-related hosted services, and registers the <see cref="RatatoskrTestHarness"/>.
    /// Also adds the test session middleware for HTTP-based session propagation.
    /// </summary>
    /// <example>
    /// <code>
    /// var factory = new WebApplicationFactory&lt;Program&gt;()
    ///     .WithRatatoskrTestServices();
    ///
    /// await using var session = factory.CreateTestSession();
    /// var client = session.CreateHttpClient();
    ///
    /// await client.PostAsync("/api/orders", content);
    /// session.Sent.ShouldContain&lt;OrderCreated&gt;();
    /// </code>
    /// </example>
    public static WebApplicationFactory<TEntryPoint> WithRatatoskrTestServices<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        Action<TestTransportOptions>? configure = null)
        where TEntryPoint : class
    {
        var options = new TestTransportOptions();
        configure?.Invoke(options);

        return factory.WithWebHostBuilder(builder =>
        {
            // Add the session middleware via IStartupFilter (runs before all other middleware)
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, TestSessionStartupFilter>();
            });

            builder.ConfigureTestServices(services =>
            {
                services.UseRatatoskrTestServices(options);
            });
        });
    }

    /// <summary>
    /// Creates a parallel-safe <see cref="WebTestSession"/> from the factory.
    /// Each session has a unique ID and provides a session-aware HTTP client
    /// that tags all messages published during requests with the session ID.
    /// </summary>
    /// <example>
    /// <code>
    /// await using var session = factory.CreateTestSession();
    /// var client = session.CreateHttpClient();
    ///
    /// await client.PostAsJsonAsync("/api/orders", new { ProductId = "abc" });
    /// session.Sent.ShouldContain&lt;OrderCreated&gt;(m => m.ProductId == "abc");
    /// </code>
    /// </example>
    public static WebTestSession CreateTestSession<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        var harness = factory.Services.GetRequiredService<RatatoskrTestHarness>();
        var session = harness.CreateSession();
        return new WebTestSession(session, factory.Server);
    }

    /// <summary>
    /// Gets the <see cref="RatatoskrTestHarness"/> from the factory's service provider.
    /// For parallel-safe testing, prefer <see cref="CreateTestSession{TEntryPoint}"/> instead.
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
        TestTransportOptions? options = null)
    {
        options ??= new TestTransportOptions();

        // Register the MessageSink (always needed for assertions)
        services.AddSingleton<MessageSink>(sp => new MessageSink
        {
            Registry = sp.GetService<ChannelRegistry>()
        });

        if (options.ReplaceTransport)
        {
            // Replace sender with TestTransport (captures via MessageSink + optional routing)
            services.RemoveAll<IMessageSender>();
            services.AddSingleton<IMessageSender>(sp =>
            {
                var sink = sp.GetRequiredService<MessageSink>();
                var dispatcher = options.RouteMessages
                    ? sp.GetRequiredService<MessageDispatcher>()
                    : null;
                return new TestTransport(sink, dispatcher, options);
            });

            // Replace the transport metadata enricher with a no-op
            services.RemoveAll<ITransportMessageMetadataEnricher>();
            services.AddSingleton<ITransportMessageMetadataEnricher, TestTransportMessageMetadataEnricher>();

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
                // Factory-based or instance-based registration - fall back to capture only
                services.RemoveAll<IMessageSender>();
                services.AddSingleton<IMessageSender>(sp => sp.GetRequiredService<MessageSink>());
            }
        }

        // Ensure MessageDispatcher is registered (needed for RatatoskrTestHarness and SimulateReceiveAsync)
        services.TryAddSingleton<MessageDispatcher>();

        // Register the test harness
        services.TryAddSingleton<RatatoskrTestHarness>();

        // Decorate the enricher with session support for session-scoped tracking
        services.DecorateEnricherWithSessionSupport();

        return services;
    }
}

/// <summary>
/// Startup filter that injects the <see cref="TestSessionMiddleware"/> before all other middleware.
/// </summary>
internal class TestSessionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            builder.UseMiddleware<TestSessionMiddleware>();
            next(builder);
        };
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
