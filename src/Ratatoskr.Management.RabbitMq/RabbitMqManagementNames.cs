using System.Text;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>
/// Every AMQP name the control plane uses, and the rules that make them work under least-privilege
/// permissions.
/// </summary>
/// <remarks>
/// Two rules shape all of it. Everything this process <em>declares</em> starts with its own
/// <c>{prefix}.</c>, because that is what a <c>{user}\..*</c> configure permission grants.
/// Everything another party <em>publishes to</em> is an exchange ending in <c>.inbox</c>, because
/// a <c>.*\.inbox$</c> write permission is the only cross-identity publish channel there is —
/// notably, the default exchange is not.
/// </remarks>
internal sealed class RabbitMqManagementNames(string prefix)
{
    /// <summary>The resource prefix this process declares under.</summary>
    public string Prefix { get; } = prefix;

    /// <summary>
    /// This agent's command entry point. Ends in <c>.inbox</c>, so a dashboard authenticating as a
    /// different identity may publish to it. Direct, so the routing key chooses between addressing
    /// the logical service and addressing one replica.
    /// </summary>
    public string CommandExchange => $"{Prefix}.mgmt.cmd.inbox";

    /// <summary>
    /// The durable queue every replica of a service consumes from. Replicas compete, so exactly one
    /// handles each logical-service command, and a replica that dies takes no traffic with it.
    /// </summary>
    public string ServiceQueue(string serviceName) =>
        $"{Prefix}.mgmt.cmd.{Sanitize(serviceName)}.q";

    /// <summary>
    /// One replica's own queue. Exclusive, so the broker removes it the moment the replica
    /// disconnects — which is what makes an instance-targeted command to a dead replica come back
    /// as unroutable instead of waiting out its deadline.
    /// </summary>
    public string InstanceQueue(string serviceName, string instanceId) =>
        $"{Prefix}.mgmt.cmd.{Sanitize(serviceName)}.{Sanitize(instanceId)}.q";

    /// <summary>This dashboard's heartbeat sink. Fanout, so every replica sees every heartbeat.</summary>
    public string DiscoveryExchange => $"{Prefix}.mgmt.discovery.inbox";

    /// <summary>This dashboard replica's private heartbeat queue.</summary>
    public string DiscoveryQueue(string replicaId) =>
        $"{Prefix}.mgmt.discovery.{Sanitize(replicaId)}.q";

    /// <summary>This dashboard's reply sink.</summary>
    public string ReplyExchange => $"{Prefix}.mgmt.reply.inbox";

    /// <summary>This dashboard replica's private reply queue.</summary>
    public string ReplyQueue(string replicaId) => $"{Prefix}.mgmt.reply.{Sanitize(replicaId)}.q";

    /// <summary>The routing key that addresses a logical service.</summary>
    public static string ServiceKey(string serviceName) => $"svc.{Sanitize(serviceName)}";

    /// <summary>The routing key that addresses one replica.</summary>
    public static string InstanceKey(string instanceId) => $"inst.{Sanitize(instanceId)}";

    /// <summary>The routing key a dashboard replica binds its reply queue with.</summary>
    public static string ReplyKey(string replicaId) => Sanitize(replicaId);

    /// <summary>
    /// Reduces a name to characters that are unambiguous in an AMQP name and in the dotted naming
    /// scheme. A service called "Orders/EU" would otherwise produce a name whose structure no
    /// longer matches the permission patterns.
    /// </summary>
    internal static string Sanitize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '-'
            );
        }

        return builder.ToString();
    }

    /// <summary>
    /// Derives the prefix from the connection user name when the host did not configure one.
    /// </summary>
    public static RabbitMqManagementNames Create(RabbitMqManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prefix = options.ResourcePrefix ?? UserNameOf(options.ConnectionString);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new InvalidOperationException(
                "RabbitMQ management needs a ResourcePrefix, or a connection string carrying a user name. "
                    + "The prefix is what every declared resource is named under, and what the broker's "
                    + "configure permission is granted on."
            );
        }

        return new RabbitMqManagementNames(Sanitize(prefix));
    }

    private static string? UserNameOf(Uri? connectionString)
    {
        var userInfo = connectionString?.UserInfo;
        if (string.IsNullOrWhiteSpace(userInfo))
        {
            return null;
        }

        var separator = userInfo.IndexOf(':', StringComparison.Ordinal);
        return Uri.UnescapeDataString(separator < 0 ? userInfo : userInfo[..separator]);
    }
}

/// <summary>The keys a RabbitMQ address carries in a service announcement.</summary>
internal static class RabbitMqAddressKeys
{
    /// <summary>The agent's command exchange.</summary>
    public const string Exchange = "exchange";

    /// <summary>The routing key that reaches any replica of the service.</summary>
    public const string ServiceKey = "serviceKey";

    /// <summary>The routing key that reaches this one replica.</summary>
    public const string InstanceKey = "instanceKey";
}
