using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure;

/// <summary>Correlation for playground runs: payload field + CloudEvents extension mirror.</summary>
public static class PlaygroundCorrelation
{
    public const string CloudEventsExtensionKey = "ratatoskrplayground_scenariorun";

    public static void AttachToMessageProperties(MessageProperties properties, string scenarioRunId)
    {
        if (string.IsNullOrEmpty(scenarioRunId))
        {
            return;
        }

        properties.CloudEventExtensions[CloudEventsExtensionKey] = scenarioRunId;
    }
}
