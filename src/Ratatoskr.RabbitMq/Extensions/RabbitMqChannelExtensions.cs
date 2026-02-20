using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;

namespace Ratatoskr.RabbitMq.Extensions;

public static class RabbitMqChannelExtensions
{
    extension(ChannelRegistration registration)
    {
        public bool IsRabbitMqChannel() => registration.GetExtension<RabbitMqChannelOptions>() != null;

        public RabbitMqChannelOptions? GetRabbitMqChannelOptions() => registration.GetExtension<RabbitMqChannelOptions>();
    }

    extension(ChannelBuilder builder)
    {
        /// <summary>
        /// Configures RabbitMQ-specific options for this channel, including exchange,
        /// queue, and retry settings.
        /// </summary>
        public ChannelBuilder WithRabbitMq(Action<RabbitMqChannelOptions> configure)
        {
            var options = new RabbitMqChannelOptions();
            configure(options);
            builder.WithExtension(options);
            return builder;
        }
    }
}
