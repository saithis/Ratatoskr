using Ratatoskr.Core;

namespace Ratatoskr.Config;

public class ConsumeChannelBuilder(ChannelRegistration channel) : ChannelBuilder(channel)
{
    /// <summary>
    /// Registers a message type that is consumed from this channel.
    /// Used for CommandConsume and EventConsume channels.
    /// </summary>
    public ConsumeChannelBuilder Consumes<T>(Action<MessageBuilder>? configure = null)
    {
        AddMessage<T>(configure);
        return this;
    }
}
