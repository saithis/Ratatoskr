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

        string host;
        if (!string.IsNullOrEmpty(rabbitMqOptions.ConnectionString))
        {
            // Parse host from connection string (amqp://user:pass@host:port/vhost)
            var uri = new Uri(rabbitMqOptions.ConnectionString);
            host = uri.Port is 5672 or -1
                ? uri.Host
                : $"{uri.Host}:{uri.Port}";
        }
        else
        {
            host = rabbitMqOptions.Port == 5672
                ? rabbitMqOptions.HostName
                : $"{rabbitMqOptions.HostName}:{rabbitMqOptions.Port}";
        }

        if (!string.IsNullOrEmpty(rabbitMqOptions.VirtualHost) && rabbitMqOptions.VirtualHost != "/")
            host += rabbitMqOptions.VirtualHost;

        document.Servers[ServerName] = new AsyncApiServer
        {
            Host = host,
            Protocol = "amqp",
            Description = "RabbitMQ server for message exchange.",
        };

        // Add server reference to all channels that have been added so far
        foreach (var channel in channels)
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
                    VHost = "/",
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
        // Use persistent delivery mode for all messages; consumer ack mode from options
        var consumerOpts = channel.GetRabbitMqConsumerOptions();

        operation.Bindings = new OperationBindings
        {
            Amqp = new AmqpOperationBinding
            {
                DeliveryMode = 2, // persistent
                Mandatory = false,
                Timestamp = true,
                Ack = consumerOpts != null && !consumerOpts.AutoAck,
            },
        };
    }

    public void ConfigureMessage(MessageRegistration message, ChannelRegistration channel, AsyncApiMessage asyncApiMessage)
    {
        var routingKey = message.GetRabbitMqOptions()?.RoutingKey ?? message.MessageTypeName;

        asyncApiMessage.Bindings = new MessageBindings
        {
            Amqp = new AmqpMessageBinding
            {
                ContentEncoding = asyncApiMessage.ContentType ?? "application/json",
                MessageType = message.MessageTypeName,
            },
        };

        // Add routing key as CC binding on the operation level is preferred,
        // but per-message we document it via the message binding messageType.
        // The routing key is also captured in the operation's cc list if needed.
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
            Servers = new List<AsyncApiReference> { AsyncApiReference.ToServer(ServerName) },
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
