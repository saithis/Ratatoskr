namespace Ratatoskr.Core;

public enum ChannelType
{
    /// <summary>
    /// We own this channel and publish events to it. We declare the exchange/topic.
    /// </summary>
    EventPublish,

    /// <summary>
    /// We expect this channel to exist (owned by another) and we send commands to it. We validate it.
    /// </summary>
    CommandPublish,

    /// <summary>
    /// We own this channel and consume commands from it. We declare the exchange and our queue.
    /// </summary>
    CommandConsume,

    /// <summary>
    /// We expect this channel to exist (owned by another) and consume events from it. We declare our queue and bind it.
    /// </summary>
    EventConsume,
}
