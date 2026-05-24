using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

internal sealed partial class ConsumeChannelInboxWarningHostedService(
    ConsumeChannelInboxPolicyAggregator policyAggregator,
    ILogger<ConsumeChannelInboxWarningHostedService> logger
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var warning in policyAggregator.DrainWarnings())
        {
            LogInboxPolicyWarning(logger, warning);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Warning}")]
    private static partial void LogInboxPolicyWarning(ILogger logger, string warning);
}
