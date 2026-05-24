using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.Testing;

namespace Ratatoskr.Tests.Testing;

public class ActivityTrackerTests
{
    [Test]
    public async Task PublishAndWaitAsync_WithWaitConditions_ThrowsInvalidOperationException()
    {
        // Arrange
        await using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = new MessageTracker();
        var activity = new ActivityTracker(services, tracker);

        // Act
        activity.WaitForMessage<TestMessage>(MessageStage.Dispatched);
        var act = async () => await activity.PublishAndWaitAsync(new TestMessage());

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private record TestMessage;
}
