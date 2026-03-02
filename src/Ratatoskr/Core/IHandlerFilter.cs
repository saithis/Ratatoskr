namespace Ratatoskr.Core;

/// <summary>
/// Allows infrastructure packages to exclude specific handler types from inline dispatch.
/// Used by <see cref="MessageDispatcher"/> to skip handlers that are managed externally
/// (e.g. by the inbox processor).
/// </summary>
public interface IHandlerFilter
{
    /// <summary>
    /// Returns true if the given handler type should be skipped during inline dispatch.
    /// </summary>
    bool ShouldSkip(Type handlerType);
}
