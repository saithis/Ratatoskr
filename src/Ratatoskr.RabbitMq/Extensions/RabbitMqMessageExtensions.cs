using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;

namespace Ratatoskr.RabbitMq.Extensions;

public static class RabbitMqMessageExtensions
{
    extension(MessageRegistration registration)
    {
        public RabbitMqMessageOptions EnsureRabbitMqOptions()
        {
            var existing = registration.GetExtension<RabbitMqMessageOptions>();
            if (existing != null)
            {
                return existing;
            }

            var options = new RabbitMqMessageOptions();
            registration.SetExtension(options);
            return options;
        }

        public RabbitMqMessageOptions? GetRabbitMqOptions() =>
            registration.GetExtension<RabbitMqMessageOptions>();
    }

    public static MessageBuilder WithRoutingKey(this MessageBuilder builder, string routingKey)
    {
        builder.MessageRegistration.EnsureRabbitMqOptions().WithRoutingKey(routingKey);
        return builder;
    }
}
