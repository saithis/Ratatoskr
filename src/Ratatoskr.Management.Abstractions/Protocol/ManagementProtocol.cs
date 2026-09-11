using System.Text.Json.Serialization;

namespace Ratatoskr.Management.Contracts;

/// <summary>Versioning and stable error codes for the management control-plane protocol.</summary>
public static class ManagementProtocol
{
    public static readonly ProtocolVersion Current = new(1, 0);
    public const string UnsupportedProtocolVersion = "unsupported_protocol_version";
    public const string UnsupportedOperation = "unsupported_operation";
    public const string UnsupportedCapability = "unsupported_capability";
    public const string InvalidRequest = "invalid_request";
    public const string DeadlineExceeded = "deadline_exceeded";
    public const string InternalError = "internal_error";
}

public sealed record ProtocolVersion(int Major, int Minor)
{
    public bool IsCompatibleWith(ProtocolVersion supported)
    {
        ArgumentNullException.ThrowIfNull(supported);
        return Major == supported.Major && Minor <= supported.Minor;
    }
}

public sealed record ManagementTarget(string LogicalServiceName, string? InstanceId = null, string? ResourceId = null);
public sealed record ManagementActor(string? Subject = null, string? DisplayName = null, string? AuthenticationType = null, string? CorrelationId = null);
/// <summary>Opaque JSON payload with an explicit application type.</summary>
public sealed record ManagementPayload(string Type, string? Json = null, string ContentType = "application/json");
public sealed record ManagementError(string Code, string SafeDetail, bool IsRetryable = false);

public enum ManagementResponseStatus { Succeeded, Accepted, Failed }

public sealed record ManagementRequestEnvelope
{
    public required ProtocolVersion ProtocolVersion { get; init; }
    public required string RequestId { get; init; }
    public required string OperationId { get; init; }
    public required ManagementTarget Target { get; init; }
    public required string Operation { get; init; }
    public required DateTimeOffset Deadline { get; init; }
    public ManagementActor? Actor { get; init; }
    public ManagementPayload? Payload { get; init; }

    // Transitional accessors allow the existing dispatcher to consume the versioned envelope.
    [JsonIgnore] public string Action => Operation;
    [JsonIgnore] public string TargetService => Target.LogicalServiceName;
    [JsonIgnore] public string? TargetContext => Target.ResourceId;
    [JsonIgnore] public string? PayloadJson => Payload?.Json;
}

public sealed record ManagementResponseEnvelope
{
    public required ProtocolVersion ProtocolVersion { get; init; }
    public required string RequestId { get; init; }
    public required string OperationId { get; init; }
    public required ManagementResponseStatus Status { get; init; }
    public ManagementPayload? Payload { get; init; }
    public ManagementError? Error { get; init; }
    public string? AcceptedOperationId { get; init; }
    [JsonIgnore] public bool Success => Status is ManagementResponseStatus.Succeeded or ManagementResponseStatus.Accepted;
    [JsonIgnore] public string? ErrorMessage => Error?.SafeDetail;
    [JsonIgnore] public string? PayloadJson => Payload?.Json;

    public static ManagementResponseEnvelope Ok(ManagementRequestEnvelope request, ManagementPayload? payload = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new()
        {
            ProtocolVersion = ManagementProtocol.Current, RequestId = request.RequestId, OperationId = request.OperationId,
            Status = ManagementResponseStatus.Succeeded, Payload = payload
        };
    }

    public static ManagementResponseEnvelope Failed(ManagementRequestEnvelope request, ManagementError error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(error);
        return new()
        {
            ProtocolVersion = ManagementProtocol.Current, RequestId = request.RequestId, OperationId = request.OperationId,
            Status = ManagementResponseStatus.Failed, Error = error
        };
    }

    public static ManagementResponseEnvelope Ok(string requestId, string? payloadJson = null) => new()
    {
        ProtocolVersion = ManagementProtocol.Current, RequestId = requestId, OperationId = requestId,
        Status = ManagementResponseStatus.Succeeded,
        Payload = payloadJson is null ? null : new ManagementPayload("application/json", payloadJson)
    };

    public static ManagementResponseEnvelope FromError(string requestId, string safeDetail) => new()
    {
        ProtocolVersion = ManagementProtocol.Current, RequestId = requestId, OperationId = requestId,
        Status = ManagementResponseStatus.Failed,
        Error = new ManagementError(ManagementProtocol.InternalError, safeDetail)
    };
}

public sealed record CapabilityDescriptor(string Name, ProtocolVersion Version);

public sealed record ServiceInstanceAnnouncement
{
    public required ProtocolVersion ProtocolVersion { get; init; }
    public required string LogicalServiceName { get; init; }
    public required string InstanceId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset AnnouncedAt { get; init; }
    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; init; } = [];
}

public enum ChannelIntent { Publish, Consume }
public sealed record TransportBinding(string ProviderKind, string DisplayName, IReadOnlyDictionary<string, string> Properties);
public sealed record ChannelTopology(string LogicalName, ChannelIntent Intent, IReadOnlyList<string> MessageTypes, IReadOnlyList<TransportBinding> TransportBindings);
public sealed record CursorPageRequest(string? Cursor = null, int Limit = 20);
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
