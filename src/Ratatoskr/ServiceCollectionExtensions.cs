using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.AsyncApi.Generation;
using Ratatoskr.AsyncApi.Model;
using Ratatoskr.Core;
using Ratatoskr.Serializers.Json;
using Ratatoskr.Testing;

namespace Ratatoskr;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRatatoskr(
        this IServiceCollection services,
        Action<RatatoskrBuilder>? configure = null)
    {
        var builder = new RatatoskrBuilder(services);
        configure?.Invoke(builder);

        services.AddSingleton(builder.CloudEventsOptions);

        // Register TimeProvider if not already registered (allows test overrides)
        services.TryAddSingleton(TimeProvider.System);

        // Register message properties enricher
        services.AddSingleton<IMessagePropertiesEnricher, MessagePropertiesEnricher>();

        // Register ChannelRegistry
        services.AddSingleton(builder.ChannelRegistry);

        // Register serializer
        services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();

        services.AddSingleton<IRatatoskr, Ratatoskr>();

        // AsyncAPI document generation
        services.AddSingleton(builder.AsyncApiOptions);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAsyncApiTransportBindingProvider, NullTransportBindingProvider>());
        services.AddSingleton<AsyncApiDocumentGenerator>();

        return services;
    }

    /// <summary>
    /// No-op binding provider used as a fallback when no transport-specific provider is registered.
    /// Ensures <c>IEnumerable&lt;IAsyncApiTransportBindingProvider&gt;</c> always resolves.
    /// </summary>
    private sealed class NullTransportBindingProvider : IAsyncApiTransportBindingProvider
    {
        public void ConfigureServers(AsyncApiDocument document, IEnumerable<ChannelRegistration> channels) { }
        public void ConfigureChannel(ChannelRegistration channel, AsyncApiDocument document) { }
        public void ConfigureOperation(ChannelRegistration channel, AsyncApiOperation operation) { }
        public void ConfigureMessage(MessageRegistration message, ChannelRegistration channel, AsyncApiMessage asyncApiMessage) { }
    }
}