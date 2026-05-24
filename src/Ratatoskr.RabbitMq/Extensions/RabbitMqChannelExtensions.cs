using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;

namespace Ratatoskr.RabbitMq.Extensions;

public static class RabbitMqChannelExtensions
{
    extension(ChannelRegistration registration)
    {
        public bool IsRabbitMqChannel() =>
            registration.GetExtension<RabbitMqChannelOptions>() != null;

        public RabbitMqChannelOptions? GetRabbitMqChannelOptions() =>
            registration.GetExtension<RabbitMqChannelOptions>();
    }

    extension(PublishChannelBuilder builder)
    {
        /// <summary>
        /// Configures RabbitMQ exchange options for this publish channel.
        /// Only exchange-related settings are available — queue and consumer options
        /// are restricted to consume channels.
        /// </summary>
        public PublishChannelBuilder WithRabbitMq(Action<RabbitMqExchangeOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            var inner = new RabbitMqChannelOptions();
            var options = new RabbitMqExchangeOptions(inner);
            configure(options);
            builder.WithExtension(inner);
            builder.AddTransport(RabbitMqConstants.TransportName);
            return builder;
        }
    }

    extension(ConsumeChannelBuilder builder)
    {
        /// <summary>
        /// Configures RabbitMQ options for this consume channel, including exchange,
        /// queue, consumer, and retry settings.
        /// </summary>
        public ConsumeChannelBuilder WithRabbitMq(Action<RabbitMqConsumeOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            var inner = new RabbitMqChannelOptions();
            var options = new RabbitMqConsumeOptions(inner);
            configure(options);
            builder.WithExtension(inner);
            builder.AddTransport(RabbitMqConstants.TransportName);
            return builder;
        }
    }
}
