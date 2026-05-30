using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;

namespace Ratatoskr.RabbitMq.Extensions;

/// <summary>
/// Extension methods for attaching and reading RabbitMQ message options on message registrations and builders.
/// </summary>
public static class RabbitMqMessageExtensions
{
    extension(MessageRegistration registration)
    {
        /// <summary>
        /// Returns the existing <see cref="RabbitMqMessageOptions"/> for this registration, creating and attaching a new instance if none exists.
        /// </summary>
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

        /// <summary>
        /// Returns the <see cref="RabbitMqMessageOptions"/> attached to this registration, or <see langword="null"/> if not set.
        /// </summary>
        public RabbitMqMessageOptions? GetRabbitMqOptions() =>
            registration.GetExtension<RabbitMqMessageOptions>();
    }

    /// <summary>
    /// Sets a custom routing key for the message type on this builder.
    /// </summary>
    public static MessageBuilder WithRoutingKey(this MessageBuilder builder, string routingKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.MessageRegistration.EnsureRabbitMqOptions().WithRoutingKey(routingKey);
        return builder;
    }
}
