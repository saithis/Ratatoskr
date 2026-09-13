namespace Ratatoskr.Management.Contracts;

/// <summary>
/// One named management operation. There is exactly one implementation of each operation, and it
/// is reached by all three callers — the per-service REST API, the in-process transport, and a
/// broker transport — so the three can never drift apart.
/// </summary>
public interface IManagementOperation
{
    /// <summary>The protocol operation name, for example <c>outbox.requeueMatching</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The type the request payload deserializes to. The dispatcher, not the operation, does the
    /// deserializing, so a malformed body is rejected once and uniformly.
    /// </summary>
    Type RequestType { get; }

    /// <summary>Runs the operation.</summary>
    Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Everything an operation needs that did not come from its own request body.
/// </summary>
public sealed class ManagementOperationContext
{
    /// <summary>The operation name being executed.</summary>
    public required string Operation { get; init; }

    /// <summary>The deserialized request, of the operation's <see cref="IManagementOperation.RequestType"/>.</summary>
    public required object Request { get; init; }

    /// <summary>
    /// The resource the operation runs against — for the EF Core operations, the DbContext short
    /// name. Null for operations that are service-wide.
    /// </summary>
    public string? Resource { get; init; }

    /// <summary>Who asked, when the surface that accepted the request could tell.</summary>
    public ManagementActor? Actor { get; init; }

    /// <summary>
    /// Stable across retries and redeliveries. Mutating operations persist it in the same
    /// transaction as their changes, so a duplicate delivery replays the stored result instead of
    /// mutating twice.
    /// </summary>
    public required Guid OperationId { get; init; }

    /// <summary>When the caller stops waiting. Long-running operations check it between batches.</summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>The request, typed.</summary>
    public TRequest RequestAs<TRequest>() =>
        Request is TRequest typed
            ? typed
            : throw new InvalidOperationException(
                $"Operation '{Operation}' received a {Request.GetType().Name} request but expected {typeof(TRequest).Name}."
            );
}

/// <summary>Contributes a capability that the host advertises in its announcements.</summary>
public interface IManagementCapabilityContributor
{
    /// <summary>The capabilities this contributor adds.</summary>
    IEnumerable<CapabilityDescriptor> GetCapabilities();
}

/// <summary>Contributes transport-neutral channel topology to the host's announcements.</summary>
public interface IManagementTopologyContributor
{
    /// <summary>The channels this contributor knows about.</summary>
    IEnumerable<ChannelTopology> GetChannels() => [];

    /// <summary>The channels this contributor knows about, resolved asynchronously.</summary>
    Task<IReadOnlyList<ChannelTopology>> GetChannelsAsync(
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IReadOnlyList<ChannelTopology>>([.. GetChannels()]);
}
