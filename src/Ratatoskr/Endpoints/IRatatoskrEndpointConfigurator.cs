using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.Endpoints;

/// <summary>
/// Ratatoskr-internal extension point implemented by transport packages
/// (EF Core, RabbitMQ, ...) to add their management endpoints. Not part of
/// the public API — consumers must not implement this interface.
/// </summary>
internal interface IRatatoskrEndpointConfigurator
{
    void MapEndpoints(IEndpointRouteBuilder endpoints, string policyName);
}
