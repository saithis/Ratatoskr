using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Agent;

internal sealed class DelegateManagementOperationHandler(
    string operation,
    Func<ManagementRequestEnvelope, CancellationToken, Task<ManagementResponseEnvelope>> handle
) : IManagementOperationHandler
{
    public string Operation { get; } = operation;

    public Task<ManagementResponseEnvelope> HandleAsync(
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default
    ) => handle(request, cancellationToken);
}
