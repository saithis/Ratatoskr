using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.Management;

namespace Ratatoskr.RabbitMq.Management;

internal sealed class RabbitMqEndpointConfigurator : IRatatoskrEndpointConfigurator
{
    public void MapEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet(
            "/rabbitmq/health",
            (
                [FromServices] RabbitMqConnectionManager conn,
                [FromServices] RabbitMqConsumer consumer
            ) =>
                TypedResults.Ok(
                    new RabbitMqHealthDto(
                        conn.IsConnected,
                        consumer.IsHealthy,
                        ConnectionError: null
                    )
                )
        );
    }
}
