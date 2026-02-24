using Ratatoskr.Core;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// Message sender that fails for testing retry logic
/// </summary>
public class FailingMessageSender : IMessageSender
{
    private int _callCount;
    private readonly int _failuresBeforeSuccess;

    public FailingMessageSender(int failuresBeforeSuccess = int.MaxValue)
    {
        _failuresBeforeSuccess = failuresBeforeSuccess;
    }

    public string TransportName => "rabbitmq";
    public int CallCount => _callCount;

    public Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        _callCount++;
        
        if (_callCount <= _failuresBeforeSuccess)
        {
            throw new InvalidOperationException($"Simulated failure (attempt {_callCount})");
        }
        
        return Task.CompletedTask;
    }
}
