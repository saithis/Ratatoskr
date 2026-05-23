using Ratatoskr.AsyncApi.Generation;
using Ratatoskr.AsyncApi.Model;
using Ratatoskr.AsyncApi.Model.Bindings;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq.AsyncApi;

/// <summary>
/// Adds AMQP/RabbitMQ-specific bindings to the AsyncAPI document.
/// Reads configuration from <see cref="RabbitMqOptions"/> and <see cref="RabbitMqChannelOptions"/>
/// already registered on each channel.
/// </summary>
public class RabbitMqAsyncApiBindingProvider(
    RabbitMqOptions rabbitMqOptions,
    CloudEventsOptions cloudEventsOptions
) : IAsyncApiTransportBindingProvider
{
    private const string ServerName = "rabbitmq";

    public void ConfigureServers(
        AsyncApiDocument document,
        IEnumerable<ChannelRegistration> channels
    )
    {
        if (rabbitMqOptions.ConnectionString is null)
            throw new InvalidOperationException(
                "RabbitMQ connection string is not configured. Set RabbitMqOptions.ConnectionString."
            );

        document.Servers ??= new Dictionary<string, AsyncApiServer>();

        document.Servers[ServerName] = new AsyncApiServer
        {
            Host = rabbitMqOptions.ConnectionString.Host,
            Protocol = rabbitMqOptions.ConnectionString.Scheme,
            Description = "RabbitMQ server for message exchange.",
        };

        // Add server reference to all RabbitMQ channels that have been added so far
        var serverRef = AsyncApiReference.ToServer(ServerName);
        foreach (var channel in channels.Where(c => c.IsRabbitMqChannel()))
        {
            if (document.Channels.TryGetValue(channel.ChannelName, out var asyncApiChannel))
            {
                asyncApiChannel.Servers ??= new List<AsyncApiReference>();
                if (asyncApiChannel.Servers.All(s => s.Ref != serverRef.Ref))
                    asyncApiChannel.Servers.Add(serverRef);
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
                Is = AmqpChannelType.RoutingKey,
                Exchange = new AmqpExchangeDefinition
                {
                    Name = channel.ChannelName,
                    Type = channelOpts.ExchangeType.ToAmqpExchangeType(),
                    Durable = channelOpts.ExchangeDurable,
                    AutoDelete = channelOpts.ExchangeAutoDelete,
                    VHost = NormalizeVHost(),
                },
            },
        };

        // For consumer channels, also add the subscription queue as a separate channel
        if (channelOpts.QueueName != null)
        {
            AddQueueChannel(channel, channelOpts, document);
        }
    }

    public void ConfigureOperation(ChannelRegistration channel, AsyncApiOperation operation)
    {
        if (!channel.IsRabbitMqChannel())
            return;

        var binding = new AmqpOperationBinding
        {
            DeliveryMode = AmqpDeliveryMode.Persistent,
            Mandatory = false,
            Timestamp = true,
        };

        // Document routing keys used by messages on this channel
        var routingKeys = channel
            .Messages.Select(m => m.GetRabbitMqOptions()?.RoutingKey ?? m.MessageTypeName)
            .Distinct()
            .ToList();

        if (routingKeys.Count > 0)
            binding.Cc = routingKeys;

        // Consumer-specific: acknowledge mode
        var channelOpts = channel.GetRabbitMqChannelOptions();
        if (channelOpts != null)
            binding.Ack = !channelOpts.AutoAck;

        operation.Bindings = new OperationBindings { Amqp = binding };
    }

    public void ConfigureMessage(
        MessageRegistration message,
        ChannelRegistration channel,
        AsyncApiMessage asyncApiMessage
    )
    {
        if (!channel.IsRabbitMqChannel())
            return;

        // Add CloudEvents binary mode headers schema (AMQP application-properties)
        if (cloudEventsOptions.ContentMode == CloudEventsContentMode.Binary)
        {
            asyncApiMessage.Headers = BuildBinaryModeHeadersSchema();
        }

        asyncApiMessage.Bindings = new MessageBindings
        {
            Amqp = new AmqpMessageBinding
            {
                ContentEncoding = null, // transport encoding like 'gzip', etc.
                MessageType = message.MessageTypeName,
            },
        };
    }

    /// <summary>
    /// Returns the AMQP application-properties schema documenting the CloudEvents attributes
    /// sent in binary content mode. Uses the <c>cloudEvents_</c> prefix as per the implementation
    /// in <see cref="CloudEventsAmqpConstants"/>.
    /// </summary>
    private static JsonSchema BuildBinaryModeHeadersSchema()
    {
        var prefix = CloudEventsAmqpConstants.HeaderPrefix; // "cloudEvents_"

        return new JsonSchema
        {
            Type = "object",
            Description =
                "AMQP application-properties carrying CloudEvents attributes (binary content mode).",
            Properties = new Dictionary<string, JsonSchema>
            {
                [$"{prefix}specversion"] = new JsonSchema
                {
                    Type = "string",
                    Description = "CloudEvents specification version.",
                    Enum = ["1.0"],
                },
                [$"{prefix}id"] = new JsonSchema
                {
                    Type = "string",
                    Description = "Unique identifier for the event.",
                },
                [$"{prefix}type"] = new JsonSchema
                {
                    Type = "string",
                    Description = "CloudEvent type identifier (e.g. com.example.order.created).",
                },
                [$"{prefix}source"] = new JsonSchema
                {
                    Type = "string",
                    Format = "uri-reference",
                    Description = "Identifies the context in which an event happened.",
                },
                [$"{prefix}time"] = new JsonSchema
                {
                    Type = new[] { "string", "null" },
                    Format = "date-time",
                    Description = "Timestamp of when the occurrence happened.",
                },
                [$"{prefix}datacontenttype"] = new JsonSchema
                {
                    Type = new[] { "string", "null" },
                    Description = "Content type of the data value (e.g. application/json).",
                },
                [$"{prefix}subject"] = new JsonSchema
                {
                    Type = new[] { "string", "null" },
                    Description =
                        "Describes the subject of the event in the context of the event producer.",
                },
                ["traceparent"] = new JsonSchema
                {
                    Type = new[] { "string", "null" },
                    Description = "W3C Trace Context traceparent header for distributed tracing.",
                },
                ["tracestate"] = new JsonSchema
                {
                    Type = new[] { "string", "null" },
                    Description = "W3C Trace Context tracestate header.",
                },
            },
            Required = [$"{prefix}specversion", $"{prefix}id", $"{prefix}type", $"{prefix}source"],
        };
    }

    private void AddQueueChannel(
        ChannelRegistration channel,
        RabbitMqChannelOptions channelOpts,
        AsyncApiDocument document
    )
    {
        var queueName = channelOpts.QueueName!;

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
                    Is = AmqpChannelType.Queue,
                    Queue = new AmqpQueueDefinition
                    {
                        Name = queueName,
                        Durable = channelOpts.QueueDurable,
                        Exclusive = channelOpts.QueueExclusive,
                        AutoDelete = channelOpts.QueueAutoDelete,
                        VHost = NormalizeVHost(),
                    },
                },
            },
        };

        document.Channels[queueName] = queueChannel;
    }

    private string NormalizeVHost()
    {
        if (rabbitMqOptions.ConnectionString is null)
            throw new InvalidOperationException(
                "RabbitMQ connection string is not configured. Set RabbitMqOptions.ConnectionString."
            );

        var path = rabbitMqOptions.ConnectionString.AbsolutePath.TrimStart('/');
        return string.IsNullOrEmpty(path) ? "/" : path;
    }
}
