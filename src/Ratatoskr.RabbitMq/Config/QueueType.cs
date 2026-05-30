namespace Ratatoskr.RabbitMq.Config;

/// <summary>
/// Specifies the RabbitMQ queue type to declare.
/// </summary>
public enum QueueType
{
    /// <summary>Standard classic queue backed by Mnesia.</summary>
    Classic = 0,

    /// <summary>Quorum queue providing replication and higher availability guarantees.</summary>
    Quorum = 1,
}
