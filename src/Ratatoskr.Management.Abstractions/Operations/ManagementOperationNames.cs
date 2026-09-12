namespace Ratatoskr.Management.Contracts;

/// <summary>
/// Every operation name in the v1 protocol. Both HTTP surfaces and every transport route by
/// these strings, so they are a wire contract and only ever grow.
/// </summary>
public static class ManagementOperationNames
{
    /// <summary>Counts, DbContexts, channels and capabilities. Doubles as the heartbeat body.</summary>
    public const string ServiceDescribe = "service.describe";

    /// <summary>The DbContexts this service exposes, and whether each has an inbox or outbox.</summary>
    public const string ContextsList = "contexts.list";

    /// <summary>Backlog gauges plus the last successful processing timestamps for one context.</summary>
    public const string ContextHealth = "context.health";

    /// <summary>A keyset page of outbox rows.</summary>
    public const string OutboxList = "outbox.list";

    /// <summary>One outbox row including its payload and CloudEvents metadata.</summary>
    public const string OutboxGet = "outbox.get";

    /// <summary>How many outbox rows a filter matches. Backs the destructive-action preview.</summary>
    public const string OutboxCount = "outbox.count";

    /// <summary>Requeues an explicit list of outbox ids.</summary>
    public const string OutboxRequeue = "outbox.requeue";

    /// <summary>Deletes an explicit list of outbox ids.</summary>
    public const string OutboxDelete = "outbox.delete";

    /// <summary>Requeues every outbox row matching a filter, in bounded batches.</summary>
    public const string OutboxRequeueMatching = "outbox.requeueMatching";

    /// <summary>Deletes every outbox row matching a filter, in bounded batches.</summary>
    public const string OutboxDeleteMatching = "outbox.deleteMatching";

    /// <summary>A keyset page of inbox handler rows.</summary>
    public const string InboxList = "inbox.list";

    /// <summary>One inbox handler row including its payload and sibling handlers.</summary>
    public const string InboxGet = "inbox.get";

    /// <summary>How many inbox handler rows a filter matches.</summary>
    public const string InboxCount = "inbox.count";

    /// <summary>Requeues an explicit list of inbox handler ids.</summary>
    public const string InboxRequeue = "inbox.requeue";

    /// <summary>Deletes an explicit list of inbox handler ids, cleaning up orphaned messages.</summary>
    public const string InboxDelete = "inbox.delete";

    /// <summary>Requeues every poisoned handler of one message id.</summary>
    public const string InboxRequeueMessage = "inbox.requeueMessage";

    /// <summary>Requeues every inbox handler row matching a filter, in bounded batches.</summary>
    public const string InboxRequeueMatching = "inbox.requeueMatching";

    /// <summary>Deletes every inbox handler row matching a filter, in bounded batches.</summary>
    public const string InboxDeleteMatching = "inbox.deleteMatching";

    /// <summary>Every operation name, for conformance assertions and registry validation.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ServiceDescribe,
        ContextsList,
        ContextHealth,
        OutboxList,
        OutboxGet,
        OutboxCount,
        OutboxRequeue,
        OutboxDelete,
        OutboxRequeueMatching,
        OutboxDeleteMatching,
        InboxList,
        InboxGet,
        InboxCount,
        InboxRequeue,
        InboxDelete,
        InboxRequeueMessage,
        InboxRequeueMatching,
        InboxDeleteMatching,
    ];
}
