using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ratatoskr.Management.Contracts;

/// <summary>
/// Who a request is addressed to. <see cref="InstanceId"/> selects one replica; leaving it null
/// addresses the logical service, where replicas compete so exactly one handles the command.
/// <see cref="Resource"/> names the resource within the service — for the EF Core operations,
/// the DbContext short name.
/// </summary>
public sealed record ManagementTarget(string ServiceName, string? InstanceId = null, string? Resource = null);

/// <summary>
/// Who asked. Populated from <c>HttpContext.User</c> at whichever HTTP surface received the
/// request, carried on the wire, and written to both the dashboard audit log and the agent's
/// idempotency record, so a mutation stays attributable across the broker hop.
/// </summary>
public sealed record ManagementActor(
    string? Subject = null,
    string? DisplayName = null,
    string? AuthenticationType = null,
    string? CorrelationId = null
);

/// <summary>
/// One management request.
/// </summary>
/// <remarks>
/// The payload is a <see cref="JsonElement"/> rather than bytes on purpose: the operation name
/// already determines the shape, and <c>System.Text.Json</c> base64-encodes byte payloads, so a
/// JSON body carried as bytes would arrive as base64 nested inside JSON — roughly a third larger,
/// and encoded and decoded twice on every hop.
/// </remarks>
public sealed record ManagementRequestEnvelope
{
    /// <summary>The protocol version the caller speaks.</summary>
    public required ProtocolVersion ProtocolVersion { get; init; }

    /// <summary>Correlates this attempt with its response. Changes on every retry.</summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Identifies the logical operation. Stable across retries and redeliveries, and the primary
    /// key of the agent's idempotency record, so a duplicate delivery mutates once.
    /// </summary>
    public required Guid OperationId { get; init; }

    /// <summary>Who the request is addressed to.</summary>
    public required ManagementTarget Target { get; init; }

    /// <summary>The operation name, for example <c>outbox.list</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>When the caller stops waiting. The agent must not start work past this point.</summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>Who asked, when the surface that accepted the request could tell.</summary>
    public ManagementActor? Actor { get; init; }

    /// <summary>The request body, shaped by <see cref="Operation"/>.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// Where to send the response, in a form only the transport that issued it understands.
    /// Carrying it on the request means an agent needs no reply configuration of its own.
    /// </summary>
    public string? ReplyTo { get; init; }
}

/// <summary>The response to one <see cref="ManagementRequestEnvelope"/>.</summary>
public sealed record ManagementResponseEnvelope
{
    /// <summary>The protocol version the responder speaks.</summary>
    public required ProtocolVersion ProtocolVersion { get; init; }

    /// <summary>The <see cref="ManagementRequestEnvelope.RequestId"/> being answered.</summary>
    public required string RequestId { get; init; }

    /// <summary>The <see cref="ManagementRequestEnvelope.OperationId"/> being answered.</summary>
    public required Guid OperationId { get; init; }

    /// <summary>The outcome.</summary>
    public required ManagementResultStatus Status { get; init; }

    /// <summary>The response body on success.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>The failure when <see cref="Status"/> is not Ok.</summary>
    public ManagementError? Error { get; init; }

    /// <summary>Whether the operation succeeded. Derived from <see cref="Status"/>, not sent.</summary>
    [JsonIgnore]
    public bool IsSuccess => Status is ManagementResultStatus.Ok;

    /// <summary>Builds a successful response to <paramref name="request"/>.</summary>
    public static ManagementResponseEnvelope Ok(
        ManagementRequestEnvelope request,
        JsonElement? payload = null
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ManagementResponseEnvelope
        {
            ProtocolVersion = ManagementProtocol.Current,
            RequestId = request.RequestId,
            OperationId = request.OperationId,
            Status = ManagementResultStatus.Ok,
            Payload = payload,
        };
    }

    /// <summary>Builds a failure response to <paramref name="request"/>.</summary>
    public static ManagementResponseEnvelope Failed(
        ManagementRequestEnvelope request,
        ManagementResultStatus status,
        ManagementError error
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(error);
        return new ManagementResponseEnvelope
        {
            ProtocolVersion = ManagementProtocol.Current,
            RequestId = request.RequestId,
            OperationId = request.OperationId,
            Status = status,
            Error = error,
        };
    }

    /// <summary>
    /// Builds a failure response when the request could not be parsed far enough to answer it
    /// properly — the correlation id is all that is known.
    /// </summary>
    public static ManagementResponseEnvelope Failed(
        string requestId,
        Guid operationId,
        ManagementResultStatus status,
        ManagementError error
    )
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ManagementResponseEnvelope
        {
            ProtocolVersion = ManagementProtocol.Current,
            RequestId = requestId,
            OperationId = operationId,
            Status = status,
            Error = error,
        };
    }
}
