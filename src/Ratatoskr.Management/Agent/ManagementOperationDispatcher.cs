using Microsoft.Extensions.Logging;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Agent;

public sealed class ManagementOperationDispatcher(
    IEnumerable<IManagementOperationHandler> handlers,
    ILogger<ManagementOperationDispatcher> logger
)
{
    private readonly Dictionary<string, IManagementOperationHandler> _handlers = handlers
        .GroupBy(handler => handler.Operation, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => group.Single(),
            StringComparer.Ordinal
        );

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
                new ManagementError(
                    ManagementProtocol.UnsupportedProtocolVersion,
                    "The management protocol version is not supported."
                )
            );
        }

        if (!_handlers.TryGetValue(request.Operation, out var handler))
        {
            return ManagementResponseEnvelope.Failed(
                request,
                new ManagementError(
                    ManagementProtocol.UnsupportedOperation,
                    "The operation is not supported."
                )
            );
        }

        try
        {
            return await handler.HandleAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle management operation {Operation} for {TargetService}", request.Operation, request.TargetService);
            return ManagementResponseEnvelope.Failed(
                request,
                new ManagementError(ManagementProtocol.InternalError, "The management operation failed.")
            );
        }
    }
}
