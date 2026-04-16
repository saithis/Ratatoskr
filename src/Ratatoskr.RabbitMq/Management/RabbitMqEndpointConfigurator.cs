using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.Endpoints;

namespace Ratatoskr.RabbitMq.Management;

internal sealed class RabbitMqEndpointConfigurator : IRatatoskrEndpointConfigurator
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string policyName)
    {
        endpoints.MapGet(
            $"{ManagementApiEndpointExtensions.BasePath}/rabbitmq/health",
            (RabbitMqConnectionManager conn, RabbitMqConsumer consumer) =>
                TypedResults.Ok(new RabbitMqHealthDto(conn.IsConnected, consumer.IsHealthy, null)))
            .RequireAuthorization(policyName)
            .DisableAntiforgery();
    }
}
