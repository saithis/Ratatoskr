using Microsoft.Extensions.Hosting;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// No-op hosted service used to eagerly register inbox processor triggers during startup.
/// The actual trigger registration happens in the factory delegate that creates this service.
/// </summary>
internal class InboxTriggerInitializerService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
