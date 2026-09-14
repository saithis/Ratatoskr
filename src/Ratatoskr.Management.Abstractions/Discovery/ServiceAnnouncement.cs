namespace Ratatoskr.Management.Contracts;

/// <summary>
/// What one service replica tells the control plane about itself. This is both the periodic
/// heartbeat body and the response to <c>service.describe</c>, so a dashboard that has just
/// started can ask for the same picture it would otherwise have waited a heartbeat for.
/// </summary>
public sealed record ServiceAnnouncement
{
    /// <summary>The protocol version this replica speaks.</summary>
    public required ProtocolVersion ProtocolVersion { get; init; }

    /// <summary>The logical service name, for example <c>orders</c>.</summary>
    public required string ServiceName { get; init; }

    /// <summary>This replica's identity, unique within the service.</summary>
    public required string InstanceId { get; init; }

    /// <summary>The host the replica runs on.</summary>
    public required string MachineName { get; init; }

    /// <summary>The deployment environment name, when the host exposes one.</summary>
    public string? Environment { get; init; }

    /// <summary>When this replica started. Lets the dashboard show restarts.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When this announcement was produced. Announcements older than the snapshot already held
    /// for the instance are rejected, so out-of-order delivery cannot move state backwards.
    /// </summary>
    public required DateTimeOffset AnnouncedAt { get; init; }

    /// <summary>
    /// What this replica can actually do. The dashboard hides features a service does not
    /// advertise, which is what lets a future agent on a different store or broker join without
    /// a UI change.
    /// </summary>
    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; init; } = [];

    /// <summary>Per-DbContext backlog counts.</summary>
    public IReadOnlyList<DbContextSummary> DbContexts { get; init; } = [];

    /// <summary>The channels this replica publishes to and consumes from.</summary>
    public IReadOnlyList<ChannelTopology> Channels { get; init; } = [];

    /// <summary>
    /// How to reach this replica, in a form only the transport that carried the announcement
    /// understands. Never exposed by the dashboard API.
    /// </summary>
    public ManagementAddress Address { get; init; } = ManagementAddress.Empty;
}

/// <summary>Backlog counts for one DbContext.</summary>
public sealed record DbContextSummary
{
    /// <summary>The DbContext short name, which is also its id in management routes.</summary>
    public required string Name { get; init; }

    /// <summary>Whether an outbox is configured for this context.</summary>
    public bool HasOutbox { get; init; }

    /// <summary>Whether an inbox is configured for this context.</summary>
    public bool HasInbox { get; init; }

    /// <summary>Outbox rows waiting to be sent.</summary>
    public long PendingOutbox { get; init; }

    /// <summary>Outbox rows that exhausted their retries.</summary>
    public long PoisonedOutbox { get; init; }

    /// <summary>Inbox handler rows waiting to run.</summary>
    public long PendingInbox { get; init; }

    /// <summary>Inbox handler rows that exhausted their retries.</summary>
    public long PoisonedInbox { get; init; }
}

/// <summary>A named, versioned feature a service advertises.</summary>
public sealed record CapabilityDescriptor(string Name, ProtocolVersion Version);

/// <summary>The capability names this build knows about.</summary>
public static class ManagementCapabilityNames
{
    /// <summary>The service exposes outbox queries and mutations.</summary>
    public const string Outbox = "outbox";

    /// <summary>The service exposes inbox queries and mutations.</summary>
    public const string Inbox = "inbox";

    /// <summary>The service can return message payloads and CloudEvents metadata.</summary>
    public const string Payloads = "payloads";

    /// <summary>The service supports bounded filter-driven bulk operations with a count preview.</summary>
    public const string BulkOperations = "bulk";

    /// <summary>The service persists operation ids, so a duplicate delivery mutates once.</summary>
    public const string Idempotency = "idempotency";

    /// <summary>The service exposes Dead Letter Queue statistics and requeueing.</summary>
    public const string Dlq = "dlq";
}

/// <summary>Whether a channel is published to or consumed from.</summary>
public enum ChannelIntent
{
    /// <summary>The service publishes to this channel.</summary>
    Publish,

    /// <summary>The service consumes from this channel.</summary>
    Consume,
}

/// <summary>How one channel maps onto one transport.</summary>
public sealed record TransportBinding(
    string ProviderKind,
    string DisplayName,
    IReadOnlyDictionary<string, string> Properties
);

/// <summary>Information about a physical queue associated with a channel.</summary>
public sealed record QueueTopology(
    string QueueName,
    long MessageCount,
    string? DeadLetterQueueName = null,
    long DeadLetterCount = 0
);

/// <summary>One logical channel and the message types that travel on it.</summary>
public sealed record ChannelTopology(
    string LogicalName,
    ChannelIntent Intent,
    IReadOnlyList<string> MessageTypes,
    IReadOnlyList<TransportBinding> TransportBindings,
    IReadOnlyList<QueueTopology>? Queues = null
)
{
    /// <summary>Physical queues and DLQs associated with this channel.</summary>
    public IReadOnlyList<QueueTopology> Queues { get; init; } = Queues ?? [];
}
