using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.Management.Http;

/// <summary>Controls when a mutating management request must carry an antiforgery token.</summary>
public sealed class ManagementAntiforgeryOptions
{
    /// <summary>Whether antiforgery validation runs at all. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Authentication schemes to treat as ambient in addition to the cookie schemes that are
    /// detected automatically. Add a custom scheme here if it authenticates from something the
    /// browser attaches on its own, such as a session cookie set by a reverse proxy.
    /// </summary>
    public ISet<string> AdditionalAmbientSchemes { get; } =
        new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Requires an antiforgery token on mutating requests, but only where one is meaningful.
/// </summary>
/// <remarks>
/// CSRF exists because a browser attaches ambient credentials — a cookie — to a cross-site
/// request the user never intended to make. A caller that presents an explicit credential (a
/// bearer token, an API key, a client certificate) is not exposed: nothing attaches that
/// credential on its own. Validating antiforgery for those callers would break exactly the
/// programmatic integrations the per-service REST API exists to serve, so the filter looks at how
/// the principal was actually authenticated rather than applying one blanket rule.
/// </remarks>
internal sealed class ManagementAntiforgeryFilter(
    IServiceProvider services,
    ManagementAntiforgeryOptions options
) : IEndpointFilter
{
    // Resolved on demand rather than injected. This filter is registered by the management core,
    // which also runs in hosts that have no HTTP surface at all — a dashboard driving a broker, or
    // a test exercising a transport — and those have no authentication schemes to resolve.
    private IAntiforgery Antiforgery => services.GetRequiredService<IAntiforgery>();

    private IAuthenticationSchemeProvider Schemes =>
        services.GetRequiredService<IAuthenticationSchemeProvider>();

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (options.Enabled && await UsesAmbientCredentialsAsync(context.HttpContext))
        {
            try
            {
                await Antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    detail: "This request was authenticated with a cookie and must carry a valid antiforgery token.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing antiforgery token",
                    type: ManagementProblemDetails.TypeBase + "antiforgery"
                );
            }
        }

        return await next(context);
    }

    private async Task<bool> UsesAmbientCredentialsAsync(HttpContext http)
    {
        if (http.User.Identity is not { IsAuthenticated: true, AuthenticationType: { } scheme })
        {
            return false;
        }

        if (options.AdditionalAmbientSchemes.Contains(scheme))
        {
            return true;
        }

        // The handler type is what actually says whether credentials are ambient. Matching on the
        // scheme *name* would miss a cookie scheme registered under any name but "Cookies".
        foreach (var registered in await Schemes.GetAllSchemesAsync())
        {
            if (
                string.Equals(registered.Name, scheme, StringComparison.Ordinal)
                && typeof(CookieAuthenticationHandler).IsAssignableFrom(registered.HandlerType)
            )
            {
                return true;
            }
        }

        return false;
    }
}
