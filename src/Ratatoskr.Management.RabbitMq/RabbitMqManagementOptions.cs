using Microsoft.Extensions.Options;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>Defines the bounded, ephemeral RabbitMQ topology used by the management control plane.</summary>
public sealed class RabbitMqManagementOptions
{
    /// <summary>
    /// Prefix for resources declared by this process. Defaults to the RabbitMQ user name,
    /// so all declared resources match a <c>{user}\..*</c> configure permission.
    /// </summary>
    public string? ResourcePrefix { get; set; }

    /// <summary>
    /// The dashboard discovery inbox to which this process sends heartbeats. When omitted,
    /// the local discovery inbox is used, which is appropriate when the dashboard and agent
    /// use the same RabbitMQ identity.
    /// </summary>
    public string? DiscoveryInbox { get; set; }

    /// <summary>
    /// Legacy alias for <see cref="ResourcePrefix"/>.
    /// </summary>
    [Obsolete("Use ResourcePrefix. Management no longer declares exchanges.")]
    public string? ExchangePrefix
    {
        get => ResourcePrefix;
        set => ResourcePrefix = value;
    }
    public string UiInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    public ushort PrefetchCount { get; set; } = 10;
    public ushort ConsumerConcurrency { get; set; } = 1;
    public int ResponseQueueMaxLength { get; set; } = 1000;
}

internal sealed class RabbitMqManagementOptionsValidator : IValidateOptions<RabbitMqManagementOptions>
{
    public ValidateOptionsResult Validate(string? name, RabbitMqManagementOptions options)
    {
        if ((options.ResourcePrefix is not null && string.IsNullOrWhiteSpace(options.ResourcePrefix)) ||
            (options.DiscoveryInbox is not null && string.IsNullOrWhiteSpace(options.DiscoveryInbox)) ||
            string.IsNullOrWhiteSpace(options.UiInstanceId))
        {
            return ValidateOptionsResult.Fail("RabbitMQ management resource prefix, discovery inbox, and UI instance ID must not be blank.");
        }

        return options.RequestTimeout <= TimeSpan.Zero || options.HeartbeatInterval <= TimeSpan.Zero || options.PrefetchCount == 0 || options.ConsumerConcurrency == 0 || options.ResponseQueueMaxLength <= 0
            ? ValidateOptionsResult.Fail("RabbitMQ management timeouts, limits, prefetch, and concurrency must be greater than zero.")
            : ValidateOptionsResult.Success;
    }
}
