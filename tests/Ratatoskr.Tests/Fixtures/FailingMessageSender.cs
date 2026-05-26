using Ratatoskr.Core;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// Message sender that fails for testing retry logic
/// </summary>
public class FailingMessageSender(string transportName, int failuresBeforeSuccess = int.MaxValue)
    : IMessageSender
{
    private int _callCount;

    public string TransportName => transportName;
    public int CallCount => _callCount;

    public Task SendAsync(
        byte[] content,
        MessageProperties props,
        CancellationToken cancellationToken
    )
    {
        _callCount++;

        if (_callCount <= failuresBeforeSuccess)
        {
            throw new InvalidOperationException(
                $"Simulated failure (attempt {_callCount.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            );
        }

        return Task.CompletedTask;
    }
}
