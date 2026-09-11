using Microsoft.Extensions.Options;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>Defines the bounded, ephemeral RabbitMQ topology used by the management control plane.</summary>
public sealed class RabbitMqManagementOptions
{
    public string ExchangePrefix { get; set; } = "ratatoskr-management";
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
        if (string.IsNullOrWhiteSpace(options.ExchangePrefix) || string.IsNullOrWhiteSpace(options.UiInstanceId))
        {
            return ValidateOptionsResult.Fail("RabbitMQ management exchange prefix and UI instance ID must be specified.");
        }

        return options.RequestTimeout <= TimeSpan.Zero || options.HeartbeatInterval <= TimeSpan.Zero || options.PrefetchCount == 0 || options.ConsumerConcurrency == 0 || options.ResponseQueueMaxLength <= 0
            ? ValidateOptionsResult.Fail("RabbitMQ management timeouts, limits, prefetch, and concurrency must be greater than zero.")
            : ValidateOptionsResult.Success;
    }
}
