namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Non-generic trigger interface for background processors.
/// Allows components to trigger processing without knowing the generic type parameter.
/// </summary>
internal interface IProcessorTrigger
{
    public ValueTask TriggerAsync(CancellationToken cancellationToken = default);
}
