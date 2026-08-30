using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Ratatoskr.Core;

namespace Ratatoskr.Management.Endpoints;

internal static class GetMetricsEndpoint
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    internal static IResult Handle(ChannelRegistry channelRegistry, IWebHostEnvironment env)
    {
        var uptime = DateTimeOffset.UtcNow - StartTime;
        var process = Process.GetCurrentProcess();

        var metrics = new SystemMetricsDto(
            InstanceId: string.Create(
                CultureInfo.InvariantCulture,
                $"{Environment.MachineName}-{process.Id}"
            ),
            MachineName: Environment.MachineName,
            EnvironmentName: env.EnvironmentName,
            ProcessId: process.Id,
            UptimeSeconds: (long)uptime.TotalSeconds,
            WorkingSetBytes: process.WorkingSet64,
            PublishChannelCount: channelRegistry.GetPublishChannels().Count(),
            ConsumeChannelCount: channelRegistry.GetConsumeChannels().Count()
        );

        return TypedResults.Ok(metrics);
    }
}

internal record SystemMetricsDto(
    string InstanceId,
    string MachineName,
    string EnvironmentName,
    int ProcessId,
    long UptimeSeconds,
    long WorkingSetBytes,
    int PublishChannelCount,
    int ConsumeChannelCount
);
