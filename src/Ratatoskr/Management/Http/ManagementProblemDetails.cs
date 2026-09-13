using Microsoft.AspNetCore.Http;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Http;

/// <summary>
/// Translates a management outcome into ProblemDetails. Both HTTP surfaces use this, so a caller
/// sees the same status and the same <c>type</c> URI whether it talked to a service directly or
/// went through the dashboard.
/// </summary>
public static class ManagementProblemDetails
{
    /// <summary>The base of every problem <c>type</c> URI this library emits.</summary>
    public const string TypeBase = "https://saithis.github.io/Ratatoskr/problems/";

    /// <summary>Renders a failed response as ProblemDetails.</summary>
    /// <remarks>
    /// The status comes from the error <em>code</em> first and the coarse result status second.
    /// Deadlines and transport failures both arrive as non-Ok results but mean very different
    /// things to a caller deciding whether to retry, and only the code distinguishes them.
    /// </remarks>
    public static IResult From(ManagementResponseEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var error =
            response.Error
            ?? new ManagementError(
                ManagementErrorCodes.InternalError,
                "The operation failed without reporting a reason."
            );

        return Results.Problem(
            detail: error.Detail,
            statusCode: StatusFor(error.Code, response.Status),
            title: TitleFor(error.Code, response.Status),
            type: TypeBase + error.Code,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = error.Code,
                ["retryable"] = error.IsRetryable,
                ["operationId"] = response.OperationId,
            }
        );
    }

    private static int StatusFor(string code, ManagementResultStatus status) =>
        code switch
        {
            ManagementErrorCodes.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
            ManagementErrorCodes.TransportUnavailable => StatusCodes.Status503ServiceUnavailable,
            ManagementErrorCodes.TargetUnreachable => StatusCodes.Status502BadGateway,
            ManagementErrorCodes.UnauthenticatedCaller => StatusCodes.Status401Unauthorized,
            ManagementErrorCodes.InternalError => StatusCodes.Status500InternalServerError,
            _ => status switch
            {
                ManagementResultStatus.NotFound => StatusCodes.Status404NotFound,
                ManagementResultStatus.Conflict => StatusCodes.Status409Conflict,
                ManagementResultStatus.Forbidden => StatusCodes.Status403Forbidden,
                ManagementResultStatus.Unsupported => StatusCodes.Status501NotImplemented,
                _ => StatusCodes.Status400BadRequest,
            },
        };

    private static string TitleFor(string code, ManagementResultStatus status) =>
        code switch
        {
            ManagementErrorCodes.DeadlineExceeded => "Deadline exceeded",
            ManagementErrorCodes.TransportUnavailable => "Transport unavailable",
            ManagementErrorCodes.TargetUnreachable => "Target unreachable",
            ManagementErrorCodes.UnauthenticatedCaller => "Unauthenticated caller",
            ManagementErrorCodes.FilterRequired => "Filter required",
            ManagementErrorCodes.UnboundedSearch => "Unbounded search",
            ManagementErrorCodes.InvalidCursor => "Invalid cursor",
            ManagementErrorCodes.InternalError => "Internal error",
            _ => status switch
            {
                ManagementResultStatus.NotFound => "Not found",
                ManagementResultStatus.Conflict => "Conflict",
                ManagementResultStatus.Forbidden => "Forbidden",
                ManagementResultStatus.Unsupported => "Not supported",
                _ => "Bad request",
            },
        };
}
