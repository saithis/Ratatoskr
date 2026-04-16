using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Centralises the management API's error response shapes as RFC 7807
/// <c>application/problem+json</c> payloads so every endpoint surfaces the
/// same fields for clients (UI, CLI, backend proxies) to consume.
/// </summary>
internal static class ManagementResults
{
    internal const string ProblemTypeBase = "https://ratatoskr.dev/errors/management/";

    internal static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: detail,
            type: ProblemTypeBase + "not-found");

    internal static ProblemHttpResult BadRequest(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad request",
            detail: detail,
            type: ProblemTypeBase + "bad-request");

    internal static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail,
            type: ProblemTypeBase + "conflict");
}
