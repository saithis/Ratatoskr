using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;

namespace Ratatoskr.Management.Transports;

/// <summary>
/// Feeds every transport's discovery stream into the dashboard's registry.
/// </summary>
/// <remarks>
/// One consumer task per source, each isolated: a broker that goes away must not stop the
/// in-process feed or the other broker, because during a migration between two control planes the
/// whole point is that one of them is allowed to be broken.
/// </remarks>
internal sealed partial class ManagementDiscoveryService(
    IEnumerable<IManagementDiscoverySource> sources,
    ServiceRegistry registry,
    ILogger<ManagementDiscoveryService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listeners = sources
            .Select(source => ListenAsync(source, stoppingToken))
            .ToArray();

        if (listeners.Length == 0)
        {
            return;
        }

        await Task.WhenAll(listeners);
    }

    private async Task ListenAsync(IManagementDiscoverySource source, CancellationToken stoppingToken)
    {
        // Yield first so one source's synchronous start-up work cannot delay the others.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var announcement in source.ListenAsync(stoppingToken))
                {
                    registry.Publish(source.TransportName, announcement);
                }

                // A stream that ends on its own has nothing more to say; do not spin on it.
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogDiscoveryFailed(logger, source.TransportName, ex);

                // The transport is responsible for its own reconnect backoff. This delay only
                // stops a source that fails instantly from becoming a hot loop.
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Discovery for management transport {TransportName} failed and will be retried."
    )]
    private static partial void LogDiscoveryFailed(
        ILogger logger,
        string transportName,
        Exception exception
    );
}
