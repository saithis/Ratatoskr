using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ratatoskr.Management;

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
