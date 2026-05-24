using AwesomeAssertions;
using Ratatoskr.Testing;

namespace Ratatoskr.Tests.Testing;

public class MessageTrackerTests
{
    [Test]
    public async Task Clear_CancelsPendingWaiters()
    {
        // Arrange
        var tracker = new MessageTracker();

        var waitTask = tracker.WaitForAsync(_ => false, TimeSpan.FromSeconds(30));

        // Act
        tracker.Clear();

        // Assert - the waiter should be cancelled
        Func<Task> act = async () => await waitTask;
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Test]
    public void Clear_WithNoWaiters_DoesNotThrow()
    {
        // Arrange
        var tracker = new MessageTracker();

        // Act & Assert
        var act = tracker.Clear;
        act.Should().NotThrow();
    }
}
