namespace Ratatoskr.Management.Contracts;

/// <summary>What a transport is able to do, so callers can degrade rather than fail.</summary>
[Flags]
public enum ManagementTransportCapabilities
{
    /// <summary>Nothing beyond sending a request to a single known peer.</summary>
    None = 0,

    /// <summary>Requests can address a logical service and be handled by any live replica.</summary>
    LogicalServiceTargeting = 1,

    /// <summary>Requests can address one named replica.</summary>
    InstanceTargeting = 2,

    /// <summary>The transport has a discovery feed of its own.</summary>
    Discovery = 4,
}

/// <summary>
/// One configured connection to one control plane, registered under a name. Two RabbitMQ brokers,
/// or a broker plus the in-process transport, are two registrations — which is what makes a
/// dashboard span clouds, and what makes a migration between them visible in one place.
/// </summary>
public interface IManagementTransport
{
    /// <summary>The registration name, unique within a host.</summary>
    string Name { get; }

    /// <summary>What this transport supports.</summary>
    ManagementTransportCapabilities Capabilities { get; }

    /// <summary>
    /// Sends a request and waits for its response. Never throws for a protocol-level failure:
    /// unreachable targets, expired deadlines and disconnected transports all come back as a
    /// response envelope carrying a stable <see cref="ManagementErrorCodes">code</see>.
    /// </summary>
    Task<ManagementResponseEnvelope> SendAsync(
        ManagementAddress address,
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>The discovery feed for one transport.</summary>
public interface IManagementDiscoverySource
{
    /// <summary>The name of the transport this feed belongs to.</summary>
    string TransportName { get; }

    /// <summary>
    /// Yields announcements until cancelled. Implementations bound their own buffering, so a slow
    /// consumer drops stale announcements rather than growing without limit.
    /// </summary>
    IAsyncEnumerable<ServiceAnnouncement> ListenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Dashboard-side aggregation over every registered transport.</summary>
public interface IManagementTransportRegistry
{
    /// <summary>The registered transport names, in registration order.</summary>
    IReadOnlyList<string> TransportNames { get; }

    /// <summary>Returns the named transport, or throws when it is not registered.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "Pairs with TryGet and matches idiomatic registry lookup naming."
    )]
    IManagementTransport Get(string name);

    /// <summary>Returns the named transport, or <see langword="false"/> when it is not registered.</summary>
    bool TryGet(string name, out IManagementTransport transport);
}
