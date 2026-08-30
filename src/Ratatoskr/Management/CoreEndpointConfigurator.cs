using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.Management.Endpoints;

namespace Ratatoskr.Management;

internal sealed class CoreEndpointConfigurator : IRatatoskrEndpointConfigurator
{
    public void MapEndpoints(IEndpointRouteBuilder group)
    {
        var systemGroup = group.MapGroup("/system");
        systemGroup.MapGet("/topology", GetTopologyEndpoint.Handle);
        systemGroup.MapGet("/metrics", GetMetricsEndpoint.Handle);
    }
}
