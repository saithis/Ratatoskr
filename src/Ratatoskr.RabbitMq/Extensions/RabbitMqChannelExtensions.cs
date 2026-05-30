using System.Diagnostics.CodeAnalysis;
using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;

namespace Ratatoskr.RabbitMq.Extensions;

/// <summary>
/// Extension methods for configuring RabbitMQ transport on channel registrations and builders.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive"
)]
public static class RabbitMqChannelExtensions
{
    extension(ChannelRegistration registration)
    {
        /// <summary>
        /// Returns <see langword="true"/> if this channel has RabbitMQ transport options attached.
        /// </summary>
        public bool IsRabbitMqChannel() =>
            registration.GetExtension<RabbitMqChannelOptions>() != null;

        /// <summary>
        /// Returns the <see cref="RabbitMqChannelOptions"/> attached to this channel registration, or <see langword="null"/> if not configured.
        /// </summary>
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
