using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

internal sealed class ConsumeChannelInboxWarningHostedService(
    ConsumeChannelInboxPolicyAggregator policyAggregator,
    ILogger<ConsumeChannelInboxWarningHostedService> logger
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var warning in policyAggregator.DrainWarnings())
            logger.LogWarning("{Warning}", warning);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
