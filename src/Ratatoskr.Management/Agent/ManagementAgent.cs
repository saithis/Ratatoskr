using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Agent;

/// <summary>
/// Contributes the per-resource backlog summaries that go into a service announcement.
/// Implemented by whichever storage package is installed.
/// </summary>
public interface IManagementResourceSummaryProvider
{
    /// <summary>Returns one summary per resource this provider knows about.</summary>
    Task<IReadOnlyList<DbContextSummary>> GetSummariesAsync(
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Assembles what this process tells the control plane about itself. One place builds the
/// announcement, so the periodic heartbeat and an on-demand <c>service.describe</c> can never
/// disagree about what this replica can do.
/// </summary>
public sealed class ManagementAgent(
    IServiceScopeFactory scopeFactory,
    IOptions<ManagementAgentOptions> options,
    TimeProvider timeProvider
)
{
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();

    /// <summary>The logical service name this process announces.</summary>
    public string ServiceName => options.Value.ServiceName;

    /// <summary>This replica's identity.</summary>
    public string InstanceId => options.Value.InstanceId;

    /// <summary>When this replica started.</summary>
    public DateTimeOffset StartedAt => _startedAt;

    /// <summary>
    /// Builds a fresh announcement. Backlog counts come from a live query, so an announcement is
    /// not free; transports call this on their heartbeat interval, not per message.
    /// </summary>
    public async Task<ServiceAnnouncement> DescribeAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Summary providers reach into scoped DbContexts, and the transports that call this are
        // singletons, so the scope is created here rather than captured.
        await using var scope = scopeFactory.CreateAsyncScope();

        var summaries = new List<DbContextSummary>();
        foreach (var provider in scope.ServiceProvider.GetServices<IManagementResourceSummaryProvider>())
        {
            summaries.AddRange(await provider.GetSummariesAsync(cancellationToken));
        }

        var capabilities = scope
            .ServiceProvider.GetServices<IManagementCapabilityContributor>()
            .SelectMany(contributor => contributor.GetCapabilities())
            .DistinctBy(capability => capability.Name, StringComparer.Ordinal)
            .OrderBy(capability => capability.Name, StringComparer.Ordinal)
            .ToArray();

        var channels = scope
            .ServiceProvider.GetServices<IManagementTopologyContributor>()
            .SelectMany(contributor => contributor.GetChannels())
            .OrderBy(channel => channel.LogicalName, StringComparer.Ordinal)
            .ThenBy(channel => channel.Intent)
            .ToArray();

        var agent = options.Value;
        return new ServiceAnnouncement
        {
            ProtocolVersion = ManagementProtocol.Current,
            ServiceName = agent.ServiceName,
            InstanceId = agent.InstanceId,
            MachineName = agent.MachineName,
            Environment = agent.EnvironmentName,
            StartedAt = _startedAt,
            AnnouncedAt = timeProvider.GetUtcNow(),
            Capabilities = capabilities,
            DbContexts = summaries.OrderBy(summary => summary.Name, StringComparer.Ordinal).ToArray(),
            Channels = channels,
        };
    }
}

/// <summary>Answers <c>service.describe</c> with the same announcement the heartbeat carries.</summary>
internal sealed class DescribeServiceOperation(ManagementAgent agent) : IManagementOperation
{
    public string Name => ManagementOperationNames.ServiceDescribe;

    public Type RequestType => typeof(DescribeServiceRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    ) => ManagementResult.Ok(await agent.DescribeAsync(cancellationToken));
}
