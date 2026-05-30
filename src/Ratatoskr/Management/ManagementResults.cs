using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ratatoskr.Management;

#pragma warning disable MA0182 // used by Ratatoskr.EfCore via InternalsVisibleTo
internal static class ManagementResults
#pragma warning restore MA0182
{
    internal const string ProblemTypeBase = "https://saithis.github.io/Ratatoskr/problems/";

    internal static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            type: ProblemTypeBase + "not-found"
        );

    internal static ProblemHttpResult BadRequest(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad request",
            type: ProblemTypeBase + "bad-request"
        );

    internal static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            type: ProblemTypeBase + "conflict"
        );
}
