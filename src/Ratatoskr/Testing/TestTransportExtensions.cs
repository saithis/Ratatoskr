using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Extension methods for configuring Ratatoskr with the test transport.
/// </summary>
public static class TestTransportExtensions
{
    /// <summary>
    /// Configures Ratatoskr to use the test transport instead of a real message broker.
    /// This registers <see cref="RatatoskrTestHarness"/>, <see cref="MessageSink"/>,
    /// and the test session infrastructure.
    /// </summary>
    /// <remarks>
    /// This should be called inside the <c>AddRatatoskr</c> builder callback or via
    /// <see cref="TestingServiceCollectionExtensions.AddTestRatatoskr"/>. For proper
    /// session ID propagation through the outbox pattern, use <see cref="AddTestRatatoskr"/>
    /// which also decorates the enricher.
    /// </remarks>
    /// <param name="builder">The Ratatoskr builder.</param>
    /// <param name="configure">Optional configuration for the test transport.</param>
    /// <returns>The builder for chaining.</returns>
    public static RatatoskrBuilder UseTestTransport(
        this RatatoskrBuilder builder,
        Action<TestTransportOptions>? configure = null)
    {
        var options = new TestTransportOptions();
        configure?.Invoke(options);

        return builder.UseTestTransport(options);
    }

    internal static RatatoskrBuilder UseTestTransport(
        this RatatoskrBuilder builder,
        TestTransportOptions options)
    {
        // Register the options
        builder.Services.AddSingleton(options);

        // Register core testing components
        builder.Services.AddSingleton<MessageSink>(sp => new MessageSink
        {
            Registry = sp.GetRequiredService<ChannelRegistry>()
        });

        // Register MessageDispatcher (required for processing simulated incoming messages)
        builder.Services.AddSingleton<MessageDispatcher>();

        // Register TestTransport as IMessageSender
        builder.Services.AddSingleton<IMessageSender>(sp =>
        {
            var sink = sp.GetRequiredService<MessageSink>();
            var dispatcher = options.RouteMessages
                ? sp.GetRequiredService<MessageDispatcher>()
                : null;
            return new TestTransport(sink, dispatcher, options);
        });

        // Register the harness
        builder.Services.AddSingleton<RatatoskrTestHarness>();

        // Register default metadata enricher for test transport
        builder.Services.AddSingleton<ITransportMessageMetadataEnricher, TestTransportMessageMetadataEnricher>();

        return builder;
    }

    /// <summary>
    /// Decorates the <see cref="IMessagePropertiesEnricher"/> with <see cref="TestSessionEnricher"/>
    /// to enable session ID propagation through the outbox pattern.
    /// Must be called AFTER <c>AddRatatoskr</c> to properly wrap the registered enricher.
    /// </summary>
    internal static IServiceCollection DecorateEnricherWithSessionSupport(this IServiceCollection services)
    {
        // Remove the existing enricher registration
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IMessagePropertiesEnricher));
        if (existing != null)
        {
            services.Remove(existing);
        }

        // Re-register with the decorator wrapping the concrete implementation
        services.AddSingleton<IMessagePropertiesEnricher>(sp =>
        {
            var registry = sp.GetRequiredService<ChannelRegistry>();
            var cloudEventsOptions = sp.GetRequiredService<CloudEventsOptions>();
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            var transportEnricher = sp.GetRequiredService<ITransportMessageMetadataEnricher>();
            var inner = new MessagePropertiesEnricher(registry, cloudEventsOptions, timeProvider, transportEnricher);
            return new TestSessionEnricher(inner);
        });

        return services;
    }
}

internal class TestTransportMessageMetadataEnricher : ITransportMessageMetadataEnricher
{
    public void Enrich(PublishInformation publishInformation, MessageProperties properties)
    {
        // No-op for test transport
    }
}
