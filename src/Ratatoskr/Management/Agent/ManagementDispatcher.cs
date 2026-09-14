using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Agent;

/// <summary>Runs one management request against the locally registered operations.</summary>
public interface IManagementDispatcher
{
    /// <summary>
    /// Executes <paramref name="request"/> and returns a response envelope. Never throws for a
    /// request-level failure; every outcome is a response carrying a stable error code.
    /// </summary>
    Task<ManagementResponseEnvelope> DispatchAsync(
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// The single entry point every caller funnels through: the per-service REST API, the in-process
/// transport, and every broker command consumer. Protocol negotiation, payload deserialization,
/// deadline enforcement and exception containment happen here exactly once, so no transport can
/// accidentally implement them differently.
/// </summary>
internal sealed partial class ManagementDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ManagementDispatcher> logger
) : IManagementDispatcher
{
    public async Task<ManagementResponseEnvelope> DispatchAsync(
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ProtocolVersion.IsCompatibleWith(ManagementProtocol.Current))
        {
            return ManagementResponseEnvelope.Failed(
                request,
                ManagementResultStatus.Unsupported,
                new ManagementError(
                    ManagementErrorCodes.UnsupportedProtocolVersion,
                    $"This service speaks management protocol {ManagementProtocol.Current} and cannot serve {request.ProtocolVersion}."
                )
            );
        }

        var remaining = request.Deadline - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            // The request spent its whole budget in transit. Starting work now would burn a
            // database connection producing a result nobody is still waiting for.
            return ManagementResponseEnvelope.Failed(
                request,
                ManagementResultStatus.Invalid,
                new ManagementError(
                    ManagementErrorCodes.DeadlineExceeded,
                    "The request deadline had already elapsed when it arrived.",
                    IsRetryable: true
                )
            );
        }

        // Operations touch a scoped DbContext while their callers — broker command consumers —
        // are singletons, so every execution gets its own scope and disposes it on the way out.
        await using var scope = scopeFactory.CreateAsyncScope();

        var operation = scope
            .ServiceProvider.GetServices<IManagementOperation>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, request.Operation, StringComparison.Ordinal)
            );

        if (operation is null)
        {
            return ManagementResponseEnvelope.Failed(
                request,
                ManagementResultStatus.Unsupported,
                new ManagementError(
                    ManagementErrorCodes.UnsupportedOperation,
                    $"This service does not implement operation '{request.Operation}'."
                )
            );
        }

        object? payload;
        try
        {
            payload = ManagementJson.FromElement(request.Payload, operation.RequestType);
        }
        catch (JsonException ex)
        {
            LogMalformedPayload(logger, request.Operation, ex);
            return ManagementResponseEnvelope.Failed(
                request,
                ManagementResultStatus.Invalid,
                new ManagementError(
                    ManagementErrorCodes.InvalidRequest,
                    $"The request body is not a valid '{request.Operation}' request."
                )
            );
        }

        payload ??= CreateDefault(operation.RequestType);
        if (payload is null)
        {
            return ManagementResponseEnvelope.Failed(
                request,
                ManagementResultStatus.Invalid,
                new ManagementError(
                    ManagementErrorCodes.InvalidRequest,
                    $"Operation '{request.Operation}' requires a request body."
                )
            );
        }

        return await ExecuteOperationAsync(operation, request, payload, remaining, cancellationToken);
    }

    private async Task<ManagementResponseEnvelope> ExecuteOperationAsync(
        IManagementOperation operation,
        ManagementRequestEnvelope request,
        object payload,
        TimeSpan remaining,
        CancellationToken cancellationToken
    )
    {
        var context = new ManagementOperationContext
        {
            Operation = request.Operation,
            Request = payload,
            Resource = request.Target.Resource,
            Actor = request.Actor,
            OperationId = request.OperationId,
            Deadline = request.Deadline,
        };

        using var deadlineSource = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineSource.Token
        );

        try
        {
            var result = await operation.ExecuteAsync(context, linked.Token);
            return ToEnvelope(request, result);
        }
        catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
        {
            return ManagementResponseEnvelope.Failed(
                request,
                ManagementResultStatus.Invalid,
                new ManagementError(
                    ManagementErrorCodes.DeadlineExceeded,
                    "The operation did not finish before the request deadline.",
                    IsRetryable: true
                )
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Exception text can carry connection strings, row contents and internal paths, so
            // the caller gets a code and the operator gets the detail in the log.
            LogOperationFailed(logger, request.Operation, request.OperationId, ex);
            return ManagementResponseEnvelope.Failed(
                request,
                ManagementResultStatus.Invalid,
                new ManagementError(
                    ManagementErrorCodes.InternalError,
                    "The management operation failed. See the service logs for details."
                )
            );
        }
    }

    private static ManagementResponseEnvelope ToEnvelope(
        ManagementRequestEnvelope request,
        ManagementResult result
    ) =>
        result.IsSuccess
            ? ManagementResponseEnvelope.Ok(
                request,
                result.Value is null ? null : ManagementJson.ToElement(result.Value, result.Value.GetType())
            )
            : ManagementResponseEnvelope.Failed(
                request,
                result.Status,
                result.Error
                    ?? new ManagementError(
                        ManagementErrorCodes.InternalError,
                        "The operation failed without reporting a reason."
                    )
            );

    /// <summary>
    /// Operations whose request carries only defaults — <c>service.describe</c>, say — are
    /// routinely called with no body at all. Materialising the default instead of rejecting the
    /// request keeps those callers from having to send an empty object.
    /// </summary>
    private static object? CreateDefault(Type requestType) =>
        requestType.GetConstructor(Type.EmptyTypes) is { } parameterless
            ? parameterless.Invoke(parameters: null)
            : null;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Rejecting management operation {Operation}: the request body could not be deserialized."
    )]
    private static partial void LogMalformedPayload(
        ILogger logger,
        string operation,
        Exception exception
    );

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Management operation {Operation} ({OperationId}) failed."
    )]
    private static partial void LogOperationFailed(
        ILogger logger,
        string operation,
        Guid operationId,
        Exception exception
    );
}
