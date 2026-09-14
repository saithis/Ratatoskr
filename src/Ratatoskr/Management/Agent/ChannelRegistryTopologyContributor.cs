using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Agent;

/// <summary>
/// Projects the bus's configured channels into the transport-neutral shape the dashboard renders,
/// so the topology view reflects what the process is actually wired to rather than a hand-kept
/// list that drifts from the code.
/// </summary>
/// <remarks>
/// The registry is resolved on demand rather than injected. A dashboard-only process, or a test
/// exercising a transport on its own, has a management agent but no message bus — and "no channels
/// to report" is the right answer there, not a resolution failure at startup.
/// </remarks>
internal sealed class ChannelRegistryTopologyContributor(IServiceProvider services)
    : IManagementTopologyContributor
{
    public IEnumerable<ChannelTopology> GetChannels()
    {
        if (services.GetService<ChannelRegistry>() is not { } channels)
        {
            yield break;
        }

        foreach (var channel in channels.GetPublishChannels().Concat(channels.GetConsumeChannels()))
        {
            yield return new ChannelTopology(
                channel.ChannelName,
                channel.Intent is ChannelType.EventPublish or ChannelType.CommandPublish
                    ? ChannelIntent.Publish
                    : ChannelIntent.Consume,
                [.. channel.Messages.Select(message => message.MessageTypeName)],
                [
                    .. channel.Transports.Select(transport => new TransportBinding(
                        transport,
                        transport,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                    )),
                ]
            );
        }
    }

    public async Task<IReadOnlyList<ChannelTopology>> GetChannelsAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (services.GetService<ChannelRegistry>() is not { } channels)
        {
            return [];
        }

        var resolvers = services.GetServices<IChannelQueueResolver>().ToArray();
        var result = new List<ChannelTopology>();

        foreach (var channel in channels.GetPublishChannels().Concat(channels.GetConsumeChannels()))
        {
            var intent = channel.Intent is ChannelType.EventPublish or ChannelType.CommandPublish
                ? ChannelIntent.Publish
                : ChannelIntent.Consume;
            var messageTypes = channel.Messages.Select(message => message.MessageTypeName).ToArray();

            var queues = new List<QueueTopology>();
            foreach (var resolver in resolvers)
            {
                var resolved = await resolver.ResolveQueuesAsync(
                    channel.ChannelName,
                    intent,
                    messageTypes,
                    cancellationToken
                );
                queues.AddRange(resolved);
            }

            result.Add(new ChannelTopology(
                channel.ChannelName,
                intent,
                messageTypes,
                [
                    .. channel.Transports.Select(transport => new TransportBinding(
                        transport,
                        transport,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                    )),
                ],
                queues
            ));
        }

        return result;
    }
}
