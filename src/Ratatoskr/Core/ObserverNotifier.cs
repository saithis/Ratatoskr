using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.Core;

/// <summary>
/// Extension methods for notifying <see cref="IMessageActivityObserver"/> instances.
/// Centralizes the resilient foreach-try-catch pattern so that observer failures
/// never affect the messaging pipeline.
/// </summary>
internal static partial class ObserverNotifier
{
    /// <summary>
    /// Notifies all observers of a message activity. Observer exceptions are caught
    /// and logged as warnings — they never propagate to the caller.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Observer exceptions are intentionally caught and swallowed to prevent them from crashing or interrupting the main messaging pipeline."
    )]
    public static async ValueTask NotifyAsync(
        this IEnumerable<IMessageActivityObserver> observers,
        MessageActivity activity,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(observers);

        foreach (var observer in observers)
        {
            try
            {
                await observer.OnMessageActivityAsync(activity).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogObserverFailed(logger, ex, activity.Stage);
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Message activity observer failed at the {Stage} stage"
    )]
    private static partial void LogObserverFailed(ILogger logger, Exception ex, MessageStage stage);
}
