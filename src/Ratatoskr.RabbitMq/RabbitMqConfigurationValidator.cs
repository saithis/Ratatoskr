using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Validates RabbitMQ channel configuration at build time, ensuring invalid
/// states are caught early with clear error messages.
/// </summary>
internal static class RabbitMqConfigurationValidator
{
    public static void Validate(ChannelRegistry registry, RabbitMqOptions options)
    {
        if (options.ConnectionString is null)
        {
            throw new InvalidOperationException(
                "RabbitMQ connection string is not configured. Set RabbitMqOptions.ConnectionString in UseRabbitMq()."
            );
        }

        foreach (var channel in registry.GetAllChannels())
        {
            var rmqOpts = channel.GetRabbitMqChannelOptions();
            if (rmqOpts is null)
            {
                continue; // Not a RabbitMQ channel
            }

            ValidateChannel(channel, rmqOpts);
        }
    }

    private static void ValidateChannel(ChannelRegistration channel, RabbitMqChannelOptions opts)
    {
        var isConsume = channel.Intent is ChannelType.CommandConsume or ChannelType.EventConsume;

        // Every channel must have at least one message registered
        if (channel.Messages.Count == 0)
        {
            throw new InvalidOperationException(
                $"Channel '{channel.ChannelName}' has no messages registered. "
                    + $"Add at least one message using Produces<T>() or Consumes<T>()."
            );
        }

        // Consume channels must have a queue name
        if (isConsume && string.IsNullOrWhiteSpace(opts.QueueName))
        {
            throw new InvalidOperationException(
                $"Consume channel '{channel.ChannelName}' does not have a QueueName configured. "
                    + $"Call .WithQueueName(\"my-queue\") in the WithRabbitMq() configuration."
            );
        }

        // Quorum queue constraints
        if (isConsume && opts.QueueType == QueueType.Quorum)
        {
            if (opts.QueueAutoDelete)
            {
                throw new InvalidOperationException(
                    $"Channel '{channel.ChannelName}' is configured with QueueType.Quorum and QueueAutoDelete=true. "
                        + $"Quorum queues do not support auto-delete. Use WithTransientQueue() with QueueType.Classic instead, "
                        + $"or remove the auto-delete setting."
                );
            }

            if (opts.QueueExclusive)
            {
                throw new InvalidOperationException(
                    $"Channel '{channel.ChannelName}' is configured with QueueType.Quorum and QueueExclusive=true. "
                        + $"Quorum queues do not support exclusive mode."
                );
            }

            if (!opts.QueueDurable)
            {
                throw new InvalidOperationException(
                    $"Channel '{channel.ChannelName}' is configured with QueueType.Quorum and QueueDurable=false. "
                        + $"Quorum queues must be durable."
                );
            }
        }

        // AutoAck with managed retry is contradictory
        if (isConsume && opts.AutoAck && opts.Retry.UseManaged)
        {
            throw new InvalidOperationException(
                $"Channel '{channel.ChannelName}' is configured with AutoAck=true and managed retry enabled. "
                    + $"Auto-acknowledged messages cannot be retried because the broker removes them immediately on delivery. "
                    + $"Either disable auto-ack with .WithAutoAck(false), or disable managed retry with .WithRetry(r => r.WithManaged(false))."
            );
        }
    }
}
