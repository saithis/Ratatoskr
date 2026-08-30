using Microsoft.AspNetCore.Http;
using Ratatoskr.Core;

namespace Ratatoskr.Management.Endpoints;

internal static class GetTopologyEndpoint
{
    internal static IResult Handle(ChannelRegistry channelRegistry)
    {
        var channels = channelRegistry
            .GetAllChannels()
            .Select(c => new ChannelTopologyDto(
                c.ChannelName,
                c.Intent.ToString(),
                c.Messages.Select(m => new MessageTopologyDto(
                        m.MessageType.FullName ?? m.MessageType.Name,
                        m.MessageTypeName,
                        m.DataSchema,
                        m.GetExtension<Config.MessageHandlerRegistrations>()
                            ?.Handlers.Select(h => new HandlerTopologyDto(
                                h.HandlerType.FullName ?? h.HandlerType.Name,
                                h.IsInbox,
                                h.InboxKey,
                                h.LegacyKeys
                            ))
                            .ToList()
                            ?? []
                    ))
                    .ToList()
            ))
            .ToList();

        return TypedResults.Ok(new TopologyResponseDto(channels));
    }
}

internal record TopologyResponseDto(IReadOnlyList<ChannelTopologyDto> Channels);

internal record ChannelTopologyDto(
    string ChannelName,
    string Intent,
    IReadOnlyList<MessageTopologyDto> Messages
);

internal record MessageTopologyDto(
    string MessageType,
    string MessageTypeName,
    string? DataSchema,
    IReadOnlyList<HandlerTopologyDto> Handlers
);

internal record HandlerTopologyDto(
    string HandlerType,
    bool IsInbox,
    string? InboxKey,
    IReadOnlyList<string> LegacyKeys
);
