using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ratatoskr.Management;

internal static class ManagementResults
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
