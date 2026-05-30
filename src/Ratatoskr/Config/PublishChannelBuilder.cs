using Ratatoskr.Core;

namespace Ratatoskr.Config;

/// <summary>
/// Fluent builder for configuring a publish channel and its outgoing message types.
/// </summary>
public sealed class PublishChannelBuilder(ChannelRegistration channel) : ChannelBuilder(channel)
{
    /// <summary>
    /// Registers a message type that is produced to this channel.
    /// Used for EventPublish and CommandPublish channels.
    /// </summary>
    public PublishChannelBuilder Produces<T>(Action<MessageBuilder>? configure = null)
    {
        AddMessage<T>(configure);
        return this;
    }
}
