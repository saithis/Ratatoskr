using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Transports;

/// <summary>The transport names this build ships.</summary>
public static class ManagementTransportKinds
{
    /// <summary>The default name for the in-process transport.</summary>
    public const string InProcess = "in-process";
}

/// <summary>Aggregates every registered transport, by name.</summary>
internal sealed class ManagementTransportRegistry : IManagementTransportRegistry
{
    private readonly Dictionary<string, IManagementTransport> _byName;

    public ManagementTransportRegistry(IEnumerable<IManagementTransport> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);

        _byName = new Dictionary<string, IManagementTransport>(StringComparer.OrdinalIgnoreCase);
        foreach (var transport in transports)
        {
            if (!_byName.TryAdd(transport.Name, transport))
            {
                throw new InvalidOperationException(
                    $"Two management transports are registered under the name '{transport.Name}'. "
                        + "Transport names identify a control plane in the dashboard and in every route, so they must be unique."
                );
            }
        }

        TransportNames = [.. _byName.Keys.Order(StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<string> TransportNames { get; }

    public IManagementTransport Get(string name) =>
        TryGet(name, out var transport)
            ? transport
            : throw new KeyNotFoundException(
                $"No management transport is registered under the name '{name}'. "
                    + $"Registered transports: {(TransportNames.Count == 0 ? "(none)" : string.Join(", ", TransportNames))}."
            );

    public bool TryGet(string name, out IManagementTransport transport)
    {
        if (!string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name, out var found))
        {
            transport = found;
            return true;
        }

        transport = null!;
        return false;
    }
}

/// <summary>
/// Tracks transport names while the container is being built, so a conflict is reported at the
/// registration call that caused it rather than as a confusing resolution failure much later.
/// </summary>
/// <remarks>
/// A name identifies one control plane, not one registration. A process that is both an agent and
/// a dashboard registers the same control plane twice — once to serve commands on it, once to
/// watch it — and that is a correct configuration, not a duplicate. What is never correct is two
/// <em>different</em> kinds of transport sharing a name, or the same side registering twice.
/// </remarks>
internal sealed class ManagementTransportNameReservations
{
    private readonly Dictionary<string, string> _kindByName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<(string Name, string Role)> _roles = new();

    /// <summary>The side of a control plane a registration takes.</summary>
    internal const string AgentRole = "agent";

    /// <summary>The side of a control plane a registration takes.</summary>
    internal const string DashboardRole = "dashboard";

    /// <summary>
    /// Claims <paramref name="name"/> for <paramref name="kind"/>. Returns whether this is the
    /// first claim, so shared registrations can skip work the other side already did.
    /// </summary>
    public bool ReserveKind(string name, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_kindByName.TryGetValue(name, out var existing))
        {
            _kindByName[name] = kind;
            return true;
        }

        if (!string.Equals(existing, kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A management transport named '{name}' is already registered as '{existing}', so it cannot also "
                    + $"be a '{kind}' transport. The name is how the dashboard, its routes and its audit records "
                    + "tell two control planes apart, so give one of them a different name."
            );
        }

        return false;
    }

    /// <summary>
    /// Claims one side of <paramref name="name"/>, rejecting a second claim of the same side.
    /// Returns whether this is the first claim on the name at all, so the caller knows whether the
    /// transport's shared services still need registering.
    /// </summary>
    public bool ReserveRole(string name, string kind, string role)
    {
        var isFirstClaim = ReserveKind(name, kind);

        if (!_roles.Add((name, role)))
        {
            throw new InvalidOperationException(
                $"Management transport '{name}' is already registered as {(role == AgentRole ? "an" : "a")} {role}. "
                    + "Register each side of a control plane once; a process that is both an agent and a dashboard "
                    + "registers the same name once per side, not twice per side."
            );
        }

        return isFirstClaim;
    }
}
