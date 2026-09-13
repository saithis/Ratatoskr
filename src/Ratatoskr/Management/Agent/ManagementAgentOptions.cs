using System.Reflection;
using Microsoft.Extensions.Options;

namespace Ratatoskr.Management.Agent;

/// <summary>
/// Identifies this process to the control plane and bounds what its operations may do.
/// </summary>
public sealed class ManagementAgentOptions
{
    private string? _serviceName;

    /// <summary>
    /// The logical service name, for example <c>orders</c>. Several replicas share it; commands
    /// addressed to it are handled by whichever replica picks them up first. Defaults to the
    /// entry assembly name.
    /// </summary>
    public string ServiceName
    {
        get =>
            _serviceName ??=
                Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant() ?? "service";
        set => _serviceName = value;
    }

    /// <summary>
    /// This replica's identity — a pod name, container id, or short guid. Must be unique within
    /// the service, because it is what an instance-targeted command routes on.
    /// </summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The host name reported in announcements.</summary>
    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>The deployment environment name reported in announcements.</summary>
    public string? EnvironmentName { get; set; } =
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

    /// <summary>How long a caller waits for a response before giving up.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often this replica announces itself. A dashboard marks a replica stale after roughly
    /// three missed intervals, so this is also what decides how quickly a dead replica is visibly
    /// dead.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The per-query database command timeout. Deliberately shorter than
    /// <see cref="RequestTimeout"/> so a slow scan fails as <c>deadline_exceeded</c> instead of
    /// pinning a connection until the caller gives up.
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The ceiling on rows one filter-driven mutation will touch before reporting
    /// <c>capped</c> and stopping. Protects an operator from a single click that rewrites a table.
    /// </summary>
    public int MaxTotalOperations { get; set; } = Contracts.ManagementPaging.DefaultMaxTotalOperations;

    /// <summary>How long a completed operation record is kept before the cleanup worker removes it.</summary>
    public TimeSpan OperationLogRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often the operation-log cleanup worker runs.</summary>
    public TimeSpan OperationLogCleanupInterval { get; set; } = TimeSpan.FromHours(1);
}

internal sealed class ManagementAgentOptionsValidator : IValidateOptions<ManagementAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, ManagementAgentOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            failures.Add("ServiceName must be set.");
        }

        if (string.IsNullOrWhiteSpace(options.InstanceId))
        {
            failures.Add("InstanceId must be set; it is what an instance-targeted command routes on.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add("RequestTimeout must be greater than zero.");
        }

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            failures.Add("HeartbeatInterval must be greater than zero.");
        }

        if (options.QueryTimeout <= TimeSpan.Zero)
        {
            failures.Add("QueryTimeout must be greater than zero.");
        }

        if (options.QueryTimeout >= options.RequestTimeout)
        {
            failures.Add(
                "QueryTimeout must be shorter than RequestTimeout so a slow query fails as "
                    + "deadline_exceeded rather than holding a database connection past the caller's deadline."
            );
        }

        if (options.MaxTotalOperations <= 0)
        {
            failures.Add("MaxTotalOperations must be greater than zero.");
        }

        if (options.OperationLogRetention <= TimeSpan.Zero)
        {
            failures.Add("OperationLogRetention must be greater than zero.");
        }

        if (options.OperationLogCleanupInterval <= TimeSpan.Zero)
        {
            failures.Add("OperationLogCleanupInterval must be greater than zero.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
