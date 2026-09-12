namespace Ratatoskr.Management.Contracts;

/// <summary>The outcome of a management operation.</summary>
public enum ManagementResultStatus
{
    /// <summary>The operation ran and produced a value.</summary>
    Ok,

    /// <summary>The addressed resource does not exist.</summary>
    NotFound,

    /// <summary>The request was rejected before it ran.</summary>
    Invalid,

    /// <summary>The target changed under the caller, or an operation id was replayed differently.</summary>
    Conflict,

    /// <summary>The caller is not permitted to run this operation.</summary>
    Forbidden,

    /// <summary>This build or this target cannot serve the operation at all.</summary>
    Unsupported,
}

/// <summary>
/// A structured, non-throwing operation outcome. Every management operation returns one of
/// these: failure is a value with a stable <see cref="ManagementErrorCodes">code</see>, never an
/// exception, so the code survives the trip across a broker and into ProblemDetails intact.
/// </summary>
public class ManagementResult
{
    /// <summary>Creates a result. Use the static factories rather than calling this directly.</summary>
    protected ManagementResult(ManagementResultStatus status, object? value, ManagementError? error)
    {
        Status = status;
        Value = value;
        Error = error;
    }

    /// <summary>The outcome.</summary>
    public ManagementResultStatus Status { get; }

    /// <summary>
    /// The value produced on success, as an object for the dispatcher to serialize.
    /// <see cref="ManagementResult{T}.Value"/> exposes it typed.
    /// </summary>
    public object? Value { get; }

    /// <summary>The failure, or <see langword="null"/> when <see cref="Status"/> is Ok.</summary>
    public ManagementError? Error { get; }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess => Status is ManagementResultStatus.Ok;

    /// <summary>A successful result carrying <paramref name="value"/>.</summary>
    public static ManagementResult<T> Ok<T>(T value) =>
        new(ManagementResultStatus.Ok, value, error: null);

    /// <summary>The addressed resource does not exist.</summary>
    public static ManagementResult NotFound(string detail, string code = ManagementErrorCodes.NotFound) =>
        Fail(ManagementResultStatus.NotFound, code, detail);

    /// <summary>The request was rejected before it ran.</summary>
    public static ManagementResult Invalid(string detail, string code = ManagementErrorCodes.InvalidRequest) =>
        Fail(ManagementResultStatus.Invalid, code, detail);

    /// <summary>The target changed under the caller.</summary>
    public static ManagementResult Conflict(string detail, string code = ManagementErrorCodes.Conflict) =>
        Fail(ManagementResultStatus.Conflict, code, detail, isRetryable: true);

    /// <summary>The caller is not permitted to run this operation.</summary>
    public static ManagementResult Forbidden(string detail, string code = ManagementErrorCodes.Forbidden) =>
        Fail(ManagementResultStatus.Forbidden, code, detail);

    /// <summary>The operation cannot be served by this build or this target.</summary>
    public static ManagementResult Unsupported(string detail, string code = ManagementErrorCodes.UnsupportedOperation) =>
        Fail(ManagementResultStatus.Unsupported, code, detail);

    /// <summary>A failure with an explicit status and code.</summary>
    public static ManagementResult Fail(
        ManagementResultStatus status,
        string code,
        string detail,
        bool isRetryable = false
    )
    {
        if (status is ManagementResultStatus.Ok)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A failure cannot be Ok.");
        }

        return new ManagementResult(status, value: null, new ManagementError(code, detail, isRetryable));
    }
}

/// <summary>A <see cref="ManagementResult"/> whose success value is typed.</summary>
public sealed class ManagementResult<T> : ManagementResult
{
    /// <summary>Creates a typed result.</summary>
    public ManagementResult(ManagementResultStatus status, T? value, ManagementError? error)
        : base(status, value, error) => Value = value;

    /// <summary>The value produced on success.</summary>
    public new T? Value { get; }
}

/// <summary>
/// A failure that is safe to hand to a caller: a stable code, a detail string written for an
/// operator, and whether retrying the identical request could succeed. Raw exception text never
/// appears here.
/// </summary>
public sealed record ManagementError(string Code, string Detail, bool IsRetryable = false);
