using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Http;

/// <summary>Shared translation between an HTTP request and a management envelope.</summary>
public static class ManagementHttp
{
    /// <summary>
    /// The operation id for this request: the caller's if it supplied one, otherwise a fresh one.
    /// </summary>
    /// <remarks>
    /// A caller that retries a mutation after a timeout must reuse the id it sent the first time,
    /// or the retry is a second mutation rather than a replay. Generating one here means the
    /// common case needs no ceremony while the careful case stays possible.
    /// </remarks>
    public static Guid ResolveOperationId(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return http.Request.Headers.TryGetValue(ManagementApiRoutes.OperationIdHeader, out var raw)
            && Guid.TryParse(raw.ToString(), out var supplied)
            ? supplied
            : Guid.NewGuid();
    }

    /// <summary>
    /// Describes who made this request, so the mutation stays attributable after it crosses a
    /// broker into another process's audit trail.
    /// </summary>
    public static ManagementActor? ActorFrom(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var identity = http.User.Identity;
        if (identity is not { IsAuthenticated: true })
        {
            return null;
        }

        return new ManagementActor(
            Subject: http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? identity.Name,
            DisplayName: identity.Name,
            AuthenticationType: identity.AuthenticationType,
            CorrelationId: Activity.Current?.Id ?? http.TraceIdentifier
        );
    }

    /// <summary>Reads a route value as a string, or null when the segment is absent.</summary>
    public static string? RouteValue(HttpContext http, string name)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.RouteValues.TryGetValue(name, out var value) ? value?.ToString() : null;
    }
}
