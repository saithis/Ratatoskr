using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ratatoskr.Management.Http;

/// <summary>
/// The authorization policies guarding the distinct management capabilities. They are separate
/// because they are genuinely different risks: reading a backlog count is not reading a customer's
/// order payload, and neither is deleting ten thousand rows.
/// </summary>
public sealed record ManagementApiPolicies(
    string ViewMetadata,
    string ViewPayloads,
    string RequeueMessages,
    string DeleteMessages,
    string BulkOperations
)
{
    /// <summary>Guards every capability with the same policy.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "'Single' refers to a single shared policy, not System.Single."
    )]
    public static ManagementApiPolicies Single(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        return new ManagementApiPolicies(policyName, policyName, policyName, policyName, policyName);
    }

    /// <summary>The distinct policy names, for startup validation.</summary>
    public IEnumerable<string> Names =>
        [ViewMetadata, ViewPayloads, RequeueMessages, DeleteMessages, BulkOperations];

    /// <summary>
    /// Fails at startup if a policy is missing, rather than at the first request — an endpoint
    /// that silently never authorizes is worse than one that refuses to start.
    /// </summary>
    public void ValidateRegistered(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var authorization = endpoints
            .ServiceProvider.GetRequiredService<IOptions<AuthorizationOptions>>()
            .Value;

        var missing = Names
            .Distinct(StringComparer.Ordinal)
            .Where(name => string.IsNullOrWhiteSpace(name) || authorization.GetPolicy(name) is null)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Ratatoskr management authorization {(missing.Length == 1 ? "policy" : "policies")} "
                    + $"{string.Join(", ", missing.Select(name => $"'{name}'"))} "
                    + "must be registered. Call services.AddAuthorization() and define them before mapping the management API."
            );
        }
    }
}
