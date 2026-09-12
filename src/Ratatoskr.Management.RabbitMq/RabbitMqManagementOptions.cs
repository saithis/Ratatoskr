using Microsoft.Extensions.Options;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>
/// One RabbitMQ control plane. Registered per transport name, so a dashboard can watch two
/// brokers at once and a service can announce on both during a migration.
/// </summary>
public sealed class RabbitMqManagementOptions
{
    /// <summary>
    /// The AMQP URI for the management connection.
    /// </summary>
    /// <remarks>
    /// Separate from the application's messaging connection on purpose. Same-broker deployments
    /// simply pass the same string — usually the same configuration key — while a deployment that
    /// wants the control plane on its own vhost, or its own broker, can have that without the
    /// messaging stack following it there.
    /// </remarks>
    public Uri? ConnectionString { get; set; }

    /// <summary>
    /// The prefix for every resource this process declares. Defaults to the connection user name,
    /// which is what a <c>{user}\..*</c> configure permission grants.
    /// </summary>
    /// <remarks>
    /// Explicit configuration rather than a derived value, because deployment identity models
    /// vary: several services may share one RabbitMQ user. Nothing assumes a prefix maps to one
    /// service — the service and instance live in routing keys, not in the prefix.
    /// </remarks>
    public string? ResourcePrefix { get; set; }

    /// <summary>
    /// The dashboard's discovery exchange this agent publishes heartbeats to.
    /// </summary>
    /// <remarks>
    /// Configured rather than discovered, because a publisher cannot declare an exchange it does
    /// not own. Leaving it unset means this process does not announce itself, which is what a
    /// dashboard-only deployment wants.
    /// </remarks>
    public string? DiscoveryExchange { get; set; }

    /// <summary>
    /// This dashboard replica's identity, used to name its private reply and discovery queues so
    /// two replicas never steal each other's replies.
    /// </summary>
    public string ReplicaId { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>How long a caller waits for a reply. Also the message TTL on commands and replies.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often this agent publishes a heartbeat.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Unacknowledged commands allowed in flight per consumer.</summary>
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// Commands handled concurrently by this agent. Bounded because every command holds a database
    /// connection for its lifetime.
    /// </summary>
    public int ConsumerConcurrency { get; set; } = 4;

    /// <summary>
    /// The broker-validated <c>user_id</c> values this agent accepts commands from.
    /// </summary>
    /// <remarks>
    /// Write access to <c>*.inbox</c> is granted to every identity in the vhost, so any of them can
    /// publish a command into this agent's command exchange. The broker refuses a publish whose
    /// <c>user_id</c> does not match the authenticated connection, which makes the property a
    /// trustworthy sender identity that costs no shared secret — but only if the agent checks it.
    /// An absent <c>user_id</c> is untrusted, never trusted.
    /// </remarks>
    public ISet<string> AllowedCallers { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// A shared secret for signing and verifying command envelopes.
    /// </summary>
    /// <remarks>
    /// For deployments where several services and the dashboard authenticate as one RabbitMQ user,
    /// so <c>user_id</c> no longer distinguishes callers. Commands then carry an HMAC over the
    /// envelope, the operation id and the deadline, and the agent refuses anything unsigned,
    /// mis-signed or expired. The operation-id record doubles as replay protection.
    /// </remarks>
    public string? SharedSecret { get; set; }

    /// <summary>
    /// Turns off caller authentication entirely.
    /// </summary>
    /// <remarks>
    /// Only safe on a vhost dedicated to management, where the set of identities that can publish
    /// is itself the access control. Never set this on a shared application vhost: it makes every
    /// service on it able to delete every other service's outbox.
    /// </remarks>
    public bool AllowUnauthenticatedCallers { get; set; }

    /// <summary>The longest backoff between retries when a receiver-owned exchange is missing.</summary>
    public TimeSpan MaxPublishBackoff { get; set; } = TimeSpan.FromSeconds(30);
}

internal sealed class RabbitMqManagementOptionsValidator
    : IValidateOptions<RabbitMqManagementOptions>
{
    public ValidateOptionsResult Validate(string? name, RabbitMqManagementOptions options)
    {
        var failures = new List<string>();
        var transport = name is null ? "the RabbitMQ management transport" : $"management transport '{name}'";

        if (options.ConnectionString is null)
        {
            failures.Add($"{transport} needs a ConnectionString.");
        }

        if (options.ResourcePrefix is { Length: 0 } or " ")
        {
            failures.Add($"{transport} has a blank ResourcePrefix. Leave it unset to use the connection user name.");
        }

        if (string.IsNullOrWhiteSpace(options.ReplicaId))
        {
            failures.Add($"{transport} needs a ReplicaId; it names this replica's private reply queue.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{transport} needs a RequestTimeout greater than zero.");
        }

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            failures.Add($"{transport} needs a HeartbeatInterval greater than zero.");
        }

        if (options.PrefetchCount == 0)
        {
            failures.Add($"{transport} needs a PrefetchCount greater than zero.");
        }

        if (options.ConsumerConcurrency <= 0)
        {
            failures.Add($"{transport} needs a ConsumerConcurrency greater than zero.");
        }

        if (options.ConsumerConcurrency > options.PrefetchCount)
        {
            failures.Add(
                $"{transport} has ConsumerConcurrency ({options.ConsumerConcurrency}) above PrefetchCount "
                    + $"({options.PrefetchCount}); the extra workers would sit idle waiting for deliveries the broker will not send."
            );
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Fails startup when an agent would accept commands from anyone on the vhost.
/// </summary>
/// <remarks>
/// Separate from the general options validator because it applies only where this process actually
/// serves commands. "Accept anything from the broker" must never be reachable by omission, so the
/// choice has to be stated: an allowlist, a shared secret, or an explicit opt-out.
/// </remarks>
internal sealed class RabbitMqManagementAgentSecurityValidator(string transportName)
    : IValidateOptions<RabbitMqManagementOptions>
{
    public ValidateOptionsResult Validate(string? name, RabbitMqManagementOptions options)
    {
        if (!string.Equals(name, transportName, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        if (
            options.AllowUnauthenticatedCallers
            || options.AllowedCallers.Count > 0
            || !string.IsNullOrWhiteSpace(options.SharedSecret)
        )
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"Management transport '{transportName}' serves commands but authenticates no caller. Every identity "
                + "in a RabbitMQ vhost may publish to a '*.inbox' exchange, so without this the broker lets any of "
                + "them delete this service's outbox. Set AllowedCallers to the dashboard's RabbitMQ user, or set "
                + "SharedSecret where identities are shared, or set AllowUnauthenticatedCallers on a vhost dedicated "
                + "to management."
        );
    }
}
