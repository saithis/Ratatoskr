namespace Ratatoskr.Management.Contracts;

/// <summary>
/// The complete set of stable error codes the control plane emits. Codes are part of the wire
/// contract: they cross the broker, appear in ProblemDetails as
/// <c>https://saithis.github.io/Ratatoskr/problems/{code}</c>, and are what a caller branches on.
/// Human-readable detail is not stable and must never be parsed.
/// </summary>
public static class ManagementErrorCodes
{
    // ── Request shape ────────────────────────────────────────────────────────

    /// <summary>The request body is missing, malformed, or fails a documented precondition.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>A destructive operation was submitted without a filter and without an id list.</summary>
    public const string FilterRequired = "filter_required";

    /// <summary>
    /// A substring search was submitted without a bounded time window or a status narrower than
    /// "all". Searching is a leading-wildcard LIKE and cannot use an index, so it must be bounded.
    /// </summary>
    public const string UnboundedSearch = "unbounded_search";

    /// <summary>The supplied pagination cursor is not a cursor this build issued.</summary>
    public const string InvalidCursor = "invalid_cursor";

    // ── Outcome ──────────────────────────────────────────────────────────────

    /// <summary>The addressed resource, message, or DbContext does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>
    /// The target moved under the caller: a concurrent modification, or a replayed operation id
    /// submitted with a different filter than the one it was started with.
    /// </summary>
    public const string Conflict = "conflict";

    /// <summary>The caller is known but not permitted to run this operation.</summary>
    public const string Forbidden = "forbidden";

    // ── Negotiation ──────────────────────────────────────────────────────────

    /// <summary>The peer speaks a protocol version this build cannot serve.</summary>
    public const string UnsupportedProtocolVersion = "unsupported_protocol_version";

    /// <summary>No operation is registered under the requested name.</summary>
    public const string UnsupportedOperation = "unsupported_operation";

    /// <summary>The operation exists but the target does not advertise the capability it needs.</summary>
    public const string UnsupportedCapability = "unsupported_capability";

    // ── Transport ────────────────────────────────────────────────────────────

    /// <summary>
    /// The addressed instance is provably gone: its queue binding no longer exists, so the
    /// command was returned unrouted rather than waiting out its deadline.
    /// </summary>
    public const string TargetUnreachable = "target_unreachable";

    /// <summary>The transport is not connected, so the request was never sent.</summary>
    public const string TransportUnavailable = "transport_unavailable";

    /// <summary>The request deadline elapsed before a response arrived.</summary>
    public const string DeadlineExceeded = "deadline_exceeded";

    /// <summary>
    /// A broker command arrived without a caller identity this agent trusts. The broker is not an
    /// authentication boundary, so an agent authenticates its callers itself.
    /// </summary>
    public const string UnauthenticatedCaller = "unauthenticated_caller";

    // ── Fallback ─────────────────────────────────────────────────────────────

    /// <summary>An unexpected failure. Detail is deliberately generic; the cause is logged.</summary>
    public const string InternalError = "internal_error";
}
