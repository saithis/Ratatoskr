using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore.Internal;

namespace Ratatoskr.Management.EfCore.Operations;

/// <summary>Lists the DbContexts this service exposes, so a client can build its navigation.</summary>
internal sealed class ListContextsOperation(ManagementResourceResolver resolver) : IManagementOperation
{
    public string Name => ManagementOperationNames.ContextsList;

    public Type RequestType => typeof(ListContextsRequest);

    public Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult<ManagementResult>(
            ManagementResult.Ok(
                new ContextListResponse(
                    [
                        .. resolver.All.Select(descriptor => new ContextListItem(
                            descriptor.Name,
                            descriptor.HasOutbox,
                            descriptor.HasInbox
                        )),
                    ]
                )
            )
        );
}

/// <summary>
/// Reports one context's backlog and when each processor last made progress.
/// </summary>
/// <remarks>
/// The counts come from the background metrics poller rather than a live <c>COUNT(*)</c>. A health
/// endpoint that runs four full-table counts is exactly the thing that falls over when the table
/// it is reporting on has grown large — which is when an operator is most likely to open it.
/// </remarks>
internal sealed class ContextHealthOperation(
    ManagementResourceResolver resolver,
    EfCoreMetricsState metrics
) : IManagementOperation
{
    public string Name => ManagementOperationNames.ContextHealth;

    public Type RequestType => typeof(ContextHealthRequest);

    public Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (resolver.ResolveAny(context, out var descriptor, out _) is { } failure)
        {
            return Task.FromResult(failure);
        }

        metrics.TryGetValue(descriptor.DbContextType, out var counts);

        return Task.FromResult<ManagementResult>(
            ManagementResult.Ok(
                new ContextHealthResponse(
                    descriptor.Name,
                    counts.PendingOutboxCount,
                    counts.PoisonedOutboxCount,
                    counts.PendingInboxCount,
                    counts.PoisonedInboxCount,
                    descriptor.LastOutboxProcessingAt,
                    descriptor.LastInboxProcessingAt
                )
            )
        );
    }
}

/// <summary>
/// Feeds the announcement's per-context backlog numbers from the metrics poller.
/// </summary>
/// <remarks>
/// Heartbeats are frequent and every replica sends them, so reading the cached gauges keeps
/// discovery from turning into a periodic count of every durability table in the fleet.
/// </remarks>
internal sealed class EfCoreResourceSummaryProvider(
    ManagementResourceResolver resolver,
    EfCoreMetricsState metrics
) : IManagementResourceSummaryProvider
{
    public Task<IReadOnlyList<DbContextSummary>> GetSummariesAsync(
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<DbContextSummary> summaries =
        [
            .. resolver.All.Select(descriptor =>
            {
                metrics.TryGetValue(descriptor.DbContextType, out var counts);
                return new DbContextSummary
                {
                    Name = descriptor.Name,
                    HasOutbox = descriptor.HasOutbox,
                    HasInbox = descriptor.HasInbox,
                    PendingOutbox = counts.PendingOutboxCount,
                    PoisonedOutbox = counts.PoisonedOutboxCount,
                    PendingInbox = counts.PendingInboxCount,
                    PoisonedInbox = counts.PoisonedInboxCount,
                };
            }),
        ];

        return Task.FromResult(summaries);
    }
}

/// <summary>
/// Advertises what this agent can actually do, so a dashboard hides tabs rather than offering a
/// button that will always fail.
/// </summary>
internal sealed class EfCoreCapabilityContributor(ManagementResourceResolver resolver)
    : IManagementCapabilityContributor
{
    public IEnumerable<CapabilityDescriptor> GetCapabilities()
    {
        var contexts = resolver.All;

        if (contexts.Any(descriptor => descriptor.HasOutbox))
        {
            yield return new CapabilityDescriptor(
                ManagementCapabilityNames.Outbox,
                ManagementProtocol.Current
            );
        }

        if (contexts.Any(descriptor => descriptor.HasInbox))
        {
            yield return new CapabilityDescriptor(
                ManagementCapabilityNames.Inbox,
                ManagementProtocol.Current
            );
        }

        if (contexts.Count == 0)
        {
            yield break;
        }

        yield return new CapabilityDescriptor(
            ManagementCapabilityNames.Payloads,
            ManagementProtocol.Current
        );
        yield return new CapabilityDescriptor(
            ManagementCapabilityNames.BulkOperations,
            ManagementProtocol.Current
        );
        yield return new CapabilityDescriptor(
            ManagementCapabilityNames.Idempotency,
            ManagementProtocol.Current
        );
    }
}
