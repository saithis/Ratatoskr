using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal static class InboxHandlerLookup
{
    /// <summary>
    /// Returns the inbox handlers that <paramref name="channel"/> runs for messages of wire type
    /// <paramref name="messageType"/>, provided the channel's inbox is stored in
    /// <paramref name="dbContextType"/>. Returns an empty list otherwise.
    /// </summary>
    public static IReadOnlyList<ChannelHandlerRegistration> GetInboxHandlersFor(
        this ChannelHandlerRegistry handlerRegistry,
        ChannelRegistration channel,
        string messageType,
        Type dbContextType
    )
    {
        var inboxConfig = channel.GetExtension<ChannelInboxConfig>();
        if (inboxConfig == null || inboxConfig.DbContextType != dbContextType)
        {
            return [];
        }

        var msgReg = channel.GetMessage(messageType);
        if (msgReg == null)
        {
            return [];
        }

        return handlerRegistry.GetInboxHandlers(channel.ChannelName, msgReg.MessageType);
    }
}
