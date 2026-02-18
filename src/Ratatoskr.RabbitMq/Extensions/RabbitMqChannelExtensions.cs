using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;

namespace Ratatoskr.RabbitMq.Extensions;

public static class RabbitMqChannelExtensions
{
    private const string TransportKey = "RabbitMqTransport";
    private const string ChannelOptionsKey = "RabbitMqChannelOptions";
    private const string ConsumerOptionsKey = "RabbitMqConsumerOptions";

    extension(ChannelRegistration registration)
    {
        public bool IsRabbitMqChannel() => registration.Metadata.ContainsKey(TransportKey);

        public RabbitMqConsumerOptions? GetRabbitMqConsumerOptions() => registration.Metadata.GetValueOrDefault(ConsumerOptionsKey) as RabbitMqConsumerOptions;

        public RabbitMqChannelOptions? GetRabbitMqChannelOptions() => registration.Metadata.GetValueOrDefault(ChannelOptionsKey) as RabbitMqChannelOptions;
    }

    extension(ChannelBuilder builder)
    {
        public ChannelBuilder WithRabbitMqExchangeOptions(Action<RabbitMqChannelOptions> configure)
        {
            // This is typically for Exchange settings (Producers and Consumers both care about Exchange type)
            // But mainly for declaration (EventPublish / CommandConsume).
            // For CommandPublish / EventConsume, we might just be validating.

            var options = new RabbitMqChannelOptions();
            configure(options);

            builder.WithMetadata(TransportKey, true);
            builder.WithMetadata(ChannelOptionsKey, options);
            return builder;
        }

        /// <summary>
        /// Configures RabbitMQ Consumer options (Queue settings).
        /// Only valid for Consumer channels.
        /// </summary>
        public ChannelBuilder WithRabbitMqConsumer(Action<RabbitMqConsumerOptions> configure)
        {
            // Valid for CommandConsume and EventConsume
            var options = new RabbitMqConsumerOptions();
            configure(options);

            builder.WithMetadata(TransportKey, true);
            builder.WithMetadata(ConsumerOptionsKey, options);
            return builder;
        }

        // We can overload WithRabbitMq to handle both if we want a unified API as per plan example?
        // Plan example: .WithRabbitMq(cfg => cfg.QueueName("...").ExchangeType(...))
        // This implies a combined options object or checks.
        // The plan had separate methods or combined?
        // Plan example: 
        // builder.AddEventConsumeChannel("users.events")
        //        .WithRabbitMq(cfg => cfg
        //             .QueueName("orders.user-handler") 
        //             .ExchangeType(ExchangeType.Topic) 
        //         )
    
        // So distinct properties on one builder, or overloaded method?
        // I can make a combined helper or just extend RabbitMqChannelOptions to include Queue stuff?
        // But Exchange params apply to the Channel (Exchange), Queue params apply to the Consumer (Queue).
        // Let's support a combined configuration closure for convenience, or strictly separate?
        // The plan showed one `.WithRabbitMq(...)` block.
        // Let's try to support that style.
        
        public ChannelBuilder WithRabbitMq(Action<RabbitMqCombinedOptions> configure)
        {
            var options = new RabbitMqCombinedOptions();
            configure(options);

            builder.WithMetadata(TransportKey, true);

            if (options.ChannelOptions != null)
                builder.WithMetadata(ChannelOptionsKey, options.ChannelOptions);

            if (options.ConsumerOptions != null)
                builder.WithMetadata(ConsumerOptionsKey, options.ConsumerOptions);

            return builder;
        }
    }
}