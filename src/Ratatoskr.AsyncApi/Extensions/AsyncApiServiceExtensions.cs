using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.AsyncApi.Config;
using Ratatoskr.AsyncApi.Generation;
using Ratatoskr.AsyncApi.Model;
using Ratatoskr.AsyncApi.RabbitMq;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.AsyncApi.Extensions;

public static class AsyncApiServiceExtensions
{
    /// <summary>
    /// Registers the AsyncAPI document generator and its options.
    /// Call <see cref="AddRabbitMqAsyncApiBindings"/> if RabbitMQ transport bindings should be included.
    /// </summary>
    public static IServiceCollection AddAsyncApiDocumentation(
        this IServiceCollection services,
        Action<AsyncApiOptions>? configure = null)
    {
        var opts = new AsyncApiOptions();
        configure?.Invoke(opts);

        services.AddSingleton(opts);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAsyncApiTransportBindingProvider, NullTransportBindingProvider>());
        services.AddSingleton<AsyncApiDocumentGenerator>();

        return services;
    }

    /// <summary>
    /// Adds RabbitMQ-specific AMQP bindings to the AsyncAPI document.
    /// Requires that <c>UseRabbitMq()</c> was called during Ratatoskr setup so that
    /// <see cref="RabbitMqOptions"/> is registered in the DI container.
    /// </summary>
    public static IServiceCollection AddRabbitMqAsyncApiBindings(this IServiceCollection services)
    {
        services.AddSingleton<IAsyncApiTransportBindingProvider, RabbitMqAsyncApiBindingProvider>();
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
