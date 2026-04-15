using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.AsyncApi.Generation;
using Ratatoskr.Core;
using Ratatoskr.Endpoints;
using Ratatoskr.RabbitMq.AsyncApi;
using Ratatoskr.RabbitMq.Management;

namespace Ratatoskr.RabbitMq.Extensions;

public static class RabbitMqRatatoskrBuilderExtensions
{
    public static RatatoskrBuilder UseRabbitMq(this RatatoskrBuilder builder, Action<RabbitMqOptions> configure)
    {
        var options = new RabbitMqOptions();
        configure.Invoke(options);

        // Register build-time validation for RabbitMQ channels
        builder.AddValidator(registry => RabbitMqConfigurationValidator.Validate(registry, options));

        // General
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<RabbitMqConnectionManager>();
        builder.Services.AddSingleton<IRabbitMqEnvelopeMapper, CloudEventsAmqpMapper>();
        builder.Services.AddSingleton<RabbitMqTopologyManager>();
        builder.Services.AddSingleton<RabbitMqTelemetry>();

        // Sending
        builder.Services.AddSingleton<ITransportMessageMetadataEnricher, RabbitMqMessageMetadataEnricher>();
        builder.Services.AddSingleton<IMessageSender, RabbitMqMessageSender>();

        // Consuming
        builder.Services.TryAddSingleton<HandlerInvoker>();
        builder.Services.TryAddSingleton<MessageDispatcher>();
        builder.Services.TryAddSingleton<MessageRouter>();
        builder.Services.AddSingleton<RabbitMqRetryHandler>();
        builder.Services.AddHostedService<RabbitMqConsumer>();

        // AsyncAPI RabbitMQ bindings
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAsyncApiTransportBindingProvider, RabbitMqAsyncApiBindingProvider>());

        // Management API
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRatatoskrEndpointConfigurator, RabbitMqEndpointConfigurator>());

        return builder;
    }
}
