using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.AsyncApi.Generation;

namespace Ratatoskr.AsyncApi.Extensions;

public static class AsyncApiEndpointExtensions
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null, // use [JsonPropertyName] attributes as-is
    };

    /// <summary>
    /// Maps a GET endpoint that returns the generated AsyncAPI v3 document as JSON.
    /// </summary>
    /// <param name="endpoints">The <see cref="IEndpointRouteBuilder"/></param>
    /// <param name="routePattern">The URL path for the AsyncAPI document. Defaults to <c>/asyncapi.json</c>.</param>
    public static IEndpointRouteBuilder MapAsyncApi(
        this IEndpointRouteBuilder endpoints,
        string routePattern = "/asyncapi.json"
    )
    {
        string? cachedJson = null;
        Lock syncLock = new();

        _ = endpoints
            .MapGet(
                routePattern,
                (AsyncApiDocumentGenerator generator) =>
                {
                    if (cachedJson is null)
                    {
                        lock (syncLock)
                        {
                            cachedJson ??= JsonSerializer.Serialize(
                                generator.Generate(),
                                _serializerOptions
                            );
                        }
                    }

                    return Results.Content(cachedJson, "application/json");
                }
            )
            .WithName("asyncapi")
            .WithDisplayName("AsyncAPI Document")
            .ExcludeFromDescription(); // exclude from Swagger UI if present

        return endpoints;
    }
}
