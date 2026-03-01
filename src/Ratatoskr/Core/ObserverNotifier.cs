using Microsoft.Extensions.Logging;

namespace Ratatoskr.Core;

/// <summary>
/// Extension methods for notifying <see cref="IMessageActivityObserver"/> instances.
/// Centralizes the resilient foreach-try-catch pattern so that observer failures
/// never affect the messaging pipeline.
/// </summary>
internal static class ObserverNotifier
{
    /// <summary>
    /// Notifies all observers of a message activity. Observer exceptions are caught
    /// and logged as warnings — they never propagate to the caller.
    /// </summary>
    public static async ValueTask NotifyAsync(
        this IEnumerable<IMessageActivityObserver> observers,
        MessageActivity activity,
        ILogger? logger = null)
    {
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnMessageActivity(activity);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Message activity observer failed at the {Stage} stage", activity.Stage);
            }
        }
    }
}
