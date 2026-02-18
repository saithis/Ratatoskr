using Ratatoskr.AsyncApi.Generation;
using Ratatoskr.AsyncApi.Model;
using Ratatoskr.AsyncApi.Model.Bindings;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.AsyncApi.RabbitMq;

/// <summary>
/// Adds AMQP/RabbitMQ-specific bindings to the AsyncAPI document.
/// Reads configuration from <see cref="RabbitMqOptions"/>, <see cref="RabbitMqChannelOptions"/>,
/// and <see cref="RabbitMqConsumerOptions"/> already registered on each channel.
/// </summary>
public class RabbitMqAsyncApiBindingProvider(RabbitMqOptions rabbitMqOptions) : IAsyncApiTransportBindingProvider
{
    private const string ServerName = "rabbitmq";

    public void ConfigureServers(AsyncApiDocument document, IEnumerable<ChannelRegistration> channels)
    {
        document.Servers ??= new Dictionary<string, AsyncApiServer>();

        document.Servers[ServerName] = new AsyncApiServer
        {
            Host = rabbitMqOptions.ConnectionString.Host,
            Protocol = rabbitMqOptions.ConnectionString.Scheme,
            Description = "RabbitMQ server for message exchange.",
        };

        // Add server reference to all RabbitMQ channels that have been added so far
        foreach (var channel in channels.Where(c => c.IsRabbitMqChannel()))
        {
            if (document.Channels.TryGetValue(channel.ChannelName, out var asyncApiChannel))
            {
                asyncApiChannel.Servers ??= new List<AsyncApiReference>();
                asyncApiChannel.Servers.Add(AsyncApiReference.ToServer(ServerName));
            }
        }
    }

    public void ConfigureChannel(ChannelRegistration channel, AsyncApiDocument document)
    {
        if (!channel.IsRabbitMqChannel())
            return;

        if (!document.Channels.TryGetValue(channel.ChannelName, out var asyncApiChannel))
            return;

        var channelOpts = channel.GetRabbitMqChannelOptions() ?? new RabbitMqChannelOptions();

        // All Ratatoskr channels are exchange-based
        asyncApiChannel.Bindings = new ChannelBindings
        {
            Amqp = new AmqpChannelBinding
            {
                Is = "routingKey",
                Exchange = new AmqpExchangeDefinition
                {
                    Name = channel.ChannelName,
                    Type = channelOpts.ExchangeType,
                    Durable = channelOpts.Durable,
                    AutoDelete = channelOpts.AutoDelete,
                    VHost = rabbitMqOptions.ConnectionString.AbsolutePath,
                },
            },
        };

        // For consumer channels, also add the subscription queue as a separate channel
        var consumerOpts = channel.GetRabbitMqConsumerOptions();
        if (consumerOpts?.QueueName != null)
        {
            AddQueueChannel(channel, consumerOpts, document);
        }
    }

    public void ConfigureOperation(ChannelRegistration channel, AsyncApiOperation operation)
    {
        if (!channel.IsRabbitMqChannel())
            return;

        var binding = new AmqpOperationBinding
        {
            DeliveryMode = 2, // persistent
            Mandatory = false,
            Timestamp = true,
        };

        // Document routing keys used by messages on this channel
        var routingKeys = channel.Messages
            .Select(m => m.GetRabbitMqOptions()?.RoutingKey ?? m.MessageTypeName)
            .Distinct()
            .ToList();

        if (routingKeys.Count > 0)
            binding.Cc = routingKeys;

        // Consumer-specific: acknowledge mode
        var consumerOpts = channel.GetRabbitMqConsumerOptions();
        if (consumerOpts != null)
            binding.Ack = !consumerOpts.AutoAck;

        operation.Bindings = new OperationBindings { Amqp = binding };
    }

    public void ConfigureMessage(MessageRegistration message, ChannelRegistration channel, AsyncApiMessage asyncApiMessage)
    {
        if (!channel.IsRabbitMqChannel())
            return;

        asyncApiMessage.Bindings = new MessageBindings
        {
            Amqp = new AmqpMessageBinding
            {
                ContentEncoding = asyncApiMessage.ContentType ?? "application/json",
                MessageType = message.MessageTypeName,
            },
        };
    }

    private void AddQueueChannel(
        ChannelRegistration channel,
        RabbitMqConsumerOptions consumerOpts,
        AsyncApiDocument document)
    {
        var queueName = consumerOpts.QueueName!;

        // Don't add if already present (e.g. multiple messages on same channel)
        if (document.Channels.ContainsKey(queueName))
            return;

        var queueChannel = new AsyncApiChannel
        {
            Address = queueName,
            Description = $"Subscription queue for {channel.ChannelName} channel events.",
            Servers = [AsyncApiReference.ToServer(ServerName)],
            Bindings = new ChannelBindings
            {
                Amqp = new AmqpChannelBinding
                {
                    Is = "queue",
                    Queue = new AmqpQueueDefinition
                    {
                        Name = queueName,
                        Durable = consumerOpts.Durable,
                        Exclusive = consumerOpts.Exclusive,
                        AutoDelete = consumerOpts.AutoDelete,
                        VHost = "/",
                    },
                },
            },
        };

        document.Channels[queueName] = queueChannel;
    }
}
