using AwesomeAssertions;
using Ratatoskr.Testing;

namespace Ratatoskr.Tests.Testing;

public class MessageTrackingSessionTests
{
    [Test]
    public async Task CollectionProperties_ReturnSameInstance()
    {
        // Arrange
        var tracker = new MessageTracker();
        await using var session = new MessageTrackingSession(tracker);

        // Act & Assert - each property should return the same cached instance
        var published1 = session.Published;
        var published2 = session.Published;
        published1.Should().BeSameAs(published2);

        var sent1 = session.Sent;
        var sent2 = session.Sent;
        sent1.Should().BeSameAs(sent2);

        var dispatched1 = session.Dispatched;
        var dispatched2 = session.Dispatched;
        dispatched1.Should().BeSameAs(dispatched2);
    }
}
