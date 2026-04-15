using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.Endpoints;

public interface IRatatoskrEndpointConfigurator
{
    void MapEndpoints(IEndpointRouteBuilder endpoints, string policyName);
}
