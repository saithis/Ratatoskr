using Ratatoskr.RabbitMq;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>Builds per-process management queues that work with least-privilege RabbitMQ permissions.</summary>
internal sealed class RabbitMqManagementResourceNames
{
    private RabbitMqManagementResourceNames(string prefix, RabbitMqManagementOptions options, string? instanceId)
    {
        CommandInbox = instanceId is null
            ? $"{prefix}.management.commands.inbox"
            : $"{prefix}.management.commands.{instanceId}.inbox";
        ReplyInbox = $"{prefix}.management.replies.{options.UiInstanceId}.inbox";
        LocalDiscoveryInbox = $"{prefix}.management.discovery.inbox";
        DiscoveryTargetInbox = options.DiscoveryInbox ?? LocalDiscoveryInbox;
    }

    public string CommandInbox { get; }
    public string ReplyInbox { get; }
    public string LocalDiscoveryInbox { get; }
    public string DiscoveryTargetInbox { get; }

    public static RabbitMqManagementResourceNames Create(RabbitMqManagementOptions options, RabbitMqOptions rabbitMqOptions, string? instanceId = null)
    {
        var prefix = options.ResourcePrefix ?? RabbitMqUserName(rabbitMqOptions);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new InvalidOperationException("RabbitMQ management requires ResourcePrefix or a RabbitMQ connection string with a user name.");
        }

        return new RabbitMqManagementResourceNames(prefix, options, instanceId);
    }

    private static string? RabbitMqUserName(RabbitMqOptions options)
    {
        var userInfo = options.ConnectionString?.UserInfo;
        if (string.IsNullOrWhiteSpace(userInfo)) return null;
        var separator = userInfo.AsSpan().IndexOf(':');
        var encodedUserName = separator < 0 ? userInfo : userInfo[..separator];
        return Uri.UnescapeDataString(encodedUserName);
    }
}
