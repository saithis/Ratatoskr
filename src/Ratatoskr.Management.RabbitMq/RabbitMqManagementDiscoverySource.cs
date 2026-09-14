using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>
/// Receives service heartbeats for one dashboard replica.
/// </summary>
/// <remarks>
/// The discovery exchange is a fanout and each replica binds its own exclusive queue, so every
/// replica sees every heartbeat. A single shared queue would make replicas compete, and each
/// heartbeat would reach exactly one of them — leaving the others showing a fleet that keeps
/// going stale for no reason.
/// </remarks>
internal sealed partial class RabbitMqManagementDiscoverySource(
    string transportName,
    RabbitMqManagementConnection connection,
    IOptionsMonitor<RabbitMqManagementOptions> optionsMonitor,
    ILogger<RabbitMqManagementDiscoverySource> logger
) : IManagementDiscoverySource
{
    public string TransportName { get; } = transportName;

    public async IAsyncEnumerable<ServiceAnnouncement> ListenAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var options = optionsMonitor.Get(TransportName);
        var names = RabbitMqManagementNames.Create(options);

        // Bounded: a dashboard that cannot keep up with a large fleet drops the oldest heartbeats
        // rather than growing a queue in memory. Dropping is safe because every heartbeat is a
        // full snapshot — the next one carries everything the dropped one did.
        var announcements = Channel.CreateBounded<ServiceAnnouncement>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }
        );

        await using var channel = await connection.CreateChannelAsync(
            publisherConfirms: false,
            cancellationToken: cancellationToken
        );

        var queue = names.DiscoveryQueue(options.ReplicaId);
        await channel.ExchangeDeclareAsync(
            names.DiscoveryExchange,
            ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken
        );
        await channel.QueueDeclareAsync(
            queue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // A heartbeat that is three intervals old has already been superseded twice.
                ["x-message-ttl"] = (long)(options.HeartbeatInterval * 3).TotalMilliseconds,
                ["x-max-length"] = 1000L,
            },
            cancellationToken: cancellationToken
        );
        await channel.QueueBindAsync(
            queue,
            names.DiscoveryExchange,
            routingKey: string.Empty,
            cancellationToken: cancellationToken
        );

        channel.ChannelShutdownAsync += (_, args) =>
        {
            announcements.Writer.TryComplete(
                new InvalidOperationException($"Discovery channel shut down ({args.ReplyCode}: {args.ReplyText}).")
            );
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            try
            {
                var announcement = JsonSerializer.Deserialize<ServiceAnnouncement>(
                    delivery.Body.Span,
                    ManagementJson.Options
                );

                if (announcement is not null)
                {
                    announcements.Writer.TryWrite(announcement);
                }
            }
            catch (JsonException ex)
            {
                // A heartbeat from a service on a newer protocol, most likely. Dropping one is
                // harmless; poisoning the consumer loop over it would not be.
                LogMalformedAnnouncement(logger, TransportName, ex);
            }

            return Task.CompletedTask;
        };

        await channel.BasicConsumeAsync(
            queue,
            autoAck: true,
            consumer: consumer,
            cancellationToken: cancellationToken
        );

        LogListening(logger, TransportName, names.DiscoveryExchange, queue);

        await foreach (var announcement in announcements.Reader.ReadAllAsync(cancellationToken))
        {
            yield return announcement;
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Management transport {TransportName} is listening for heartbeats on {Exchange} via queue {Queue}."
    )]
    private static partial void LogListening(
        ILogger logger,
        string transportName,
        string exchange,
        string queue
    );

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Management transport {TransportName} dropped a heartbeat it could not parse."
    )]
    private static partial void LogMalformedAnnouncement(
        ILogger logger,
        string transportName,
        Exception exception
    );
}
