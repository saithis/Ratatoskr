namespace Ratatoskr.Management.Contracts;

/// <summary>
/// Handles one named management operation. Implementations are registered by the
/// feature that owns the operation, so dispatch does not require a central switch.
/// </summary>
public interface IManagementOperationHandler
{
    /// <summary>The protocol operation name handled by this feature.</summary>
    string Operation { get; }

    /// <summary>Executes the operation and returns a protocol response.</summary>
    Task<ManagementResponseEnvelope> HandleAsync(
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default
    );
}
