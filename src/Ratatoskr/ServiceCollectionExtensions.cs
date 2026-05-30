using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.AsyncApi.Generation;
using Ratatoskr.AsyncApi.Model;
using Ratatoskr.Core;
using Ratatoskr.Serializers.Json;

namespace Ratatoskr;

/// <summary>
/// Extension methods for registering Ratatoskr services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers all Ratatoskr core services and applies the provided channel/transport configuration.</summary>
    public static IServiceCollection AddRatatoskr(
        this IServiceCollection services,
        Action<RatatoskrBuilder>? configure = null
    )
    {
        if (services.Any(d => d.ServiceType == typeof(RatatoskrMarker)))
        {
            throw new InvalidOperationException(
                "AddRatatoskr has already been called. It must only be called once per IServiceCollection."
            );
        }

        _ = services.AddSingleton<RatatoskrMarker>();

        var builder = new RatatoskrBuilder(services);
        configure?.Invoke(builder);

        // Run deferred actions (e.g. infrastructure packages finalizing handler registrations).
        // Must run after the full configure callback.
        builder.ExecuteDeferredActions();

        // Run all registered validators (e.g. RabbitMQ configuration validation)
        builder.Validate();

        _ = services.AddSingleton(builder.CloudEventsOptions);

        // Register TimeProvider if not already registered (allows test overrides)
        services.TryAddSingleton(TimeProvider.System);

        // Register message properties enricher
        _ = services.AddSingleton<IMessagePropertiesEnricher, MessagePropertiesEnricher>();

        // Register ChannelRegistry and ChannelHandlerRegistry
        builder.ChannelRegistry.Freeze();
        _ = services.AddSingleton(builder.ChannelRegistry);
        var handlerRegistry = ChannelHandlerRegistry.Build(builder.ChannelRegistry);
        builder.ValidateHandlers(handlerRegistry);
        _ = services.AddSingleton(handlerRegistry);

        // Register serializer (TryAdd so users can pre-register a custom IMessageSerializer)
        services.TryAddSingleton<IMessageSerializer>(
            new JsonMessageSerializer(builder.JsonSerializerOptions)
        );
        services.TryAddSingleton<IMessageSerializerResolver, MessageSerializerResolver>();

        services.TryAddSingleton<HandlerInvoker>();
        _ = services.AddSingleton<IRatatoskr, RatatoskrClient>();

        // AsyncAPI document generation
        _ = services.AddSingleton(builder.AsyncApiOptions);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IAsyncApiTransportBindingProvider,
                NullTransportBindingProvider
            >()
        );
        _ = services.AddSingleton<AsyncApiDocumentGenerator>();

        return services;
    }

    /// <summary>
    /// No-op binding provider registered as a default sentinel.
    /// Has no effect at runtime; exists so that transport providers remain optional.
    /// </summary>
    private sealed class NullTransportBindingProvider : IAsyncApiTransportBindingProvider
    {
        public void ConfigureServers(
            AsyncApiDocument document,
            IEnumerable<ChannelRegistration> channels
        ) { }

        public void ConfigureChannel(ChannelRegistration channel, AsyncApiDocument document) { }

        public void ConfigureOperation(ChannelRegistration channel, AsyncApiOperation operation) { }

        public void ConfigureMessage(
            MessageRegistration message,
            ChannelRegistration channel,
            AsyncApiMessage asyncApiMessage
        ) { }
    }

    /// <summary>
    /// Sentinel type used to detect duplicate <see cref="AddRatatoskr"/> calls.
    /// </summary>
    private sealed class RatatoskrMarker;
}
