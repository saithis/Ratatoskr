using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.Tests.Inbox;

public class InboxHandlerStatusEntityTests
{
    [Test]
    public void MarkAsFailed_CalculatesExponentialBackoffWithJitter()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider(
            new DateTimeOffset(2025, 1, 24, 12, 0, 0, TimeSpan.Zero)
        );
        var status = InboxHandlerStatusEntity.Create("msg-1", "handler-a", fakeTime);

        // Act - First failure (base = 2^1 = 2s, jitter range = [1s, 2s))
        status.MarkAsFailed("Error 1", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));

        // Assert
        status.ErrorCount.Should().Be(1);
        status.NextAttemptAt.Should().NotBeNull();
        var now1 = fakeTime.GetUtcNow();
        status.NextAttemptAt.Value.Should().BeOnOrAfter(now1.AddSeconds(1));
        status.NextAttemptAt.Value.Should().BeOnOrBefore(now1.AddSeconds(2));

        // Act - Second failure (base = 2^2 = 4s, jitter range = [2s, 4s))
        fakeTime.Advance(TimeSpan.FromSeconds(3));
        status.MarkAsFailed("Error 2", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));

        // Assert
        status.ErrorCount.Should().Be(2);
        var now2 = fakeTime.GetUtcNow();
        status.NextAttemptAt.Value.Should().BeOnOrAfter(now2.AddSeconds(2));
        status.NextAttemptAt.Value.Should().BeOnOrBefore(now2.AddSeconds(4));

        // Act - Third failure (base = 2^3 = 8s, jitter range = [4s, 8s))
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        status.MarkAsFailed("Error 3", fakeTime, maxRetries: 5, TimeSpan.FromMinutes(5));

        // Assert
        status.ErrorCount.Should().Be(3);
        var now3 = fakeTime.GetUtcNow();
        status.NextAttemptAt.Value.Should().BeOnOrAfter(now3.AddSeconds(4));
        status.NextAttemptAt.Value.Should().BeOnOrBefore(now3.AddSeconds(8));
    }

    [Test]
    public void MarkAsFailed_CapsBackoffAtMaxRetryDelay()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var status = InboxHandlerStatusEntity.Create("msg-1", "handler-a", fakeTime);
        var maxDelay = TimeSpan.FromSeconds(10);

        // Simulate many failures to hit the cap
        for (var i = 0; i < 10; i++)
        {
            status.MarkAsFailed(
                $"Error {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                fakeTime,
                maxRetries: 20,
                maxDelay
            );
            fakeTime.Advance(TimeSpan.FromSeconds(1));
        }

        // Act - One more failure (would be 2^11 = 2048 seconds without cap)
        var beforeFail = fakeTime.GetUtcNow();
        status.MarkAsFailed("Final error", fakeTime, maxRetries: 20, maxDelay);

        // Assert - Should be capped at maxDelay (10 seconds) with jitter: [5s, 10s)
        status.NextAttemptAt.Should().NotBeNull();
        status.NextAttemptAt.Value.Should().BeOnOrAfter(beforeFail.AddSeconds(5));
        status.NextAttemptAt.Value.Should().BeOnOrBefore(beforeFail.AddSeconds(10));
    }

    [Test]
    public void MarkAsFailed_AfterMaxRetries_SetsPoisoned()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var status = InboxHandlerStatusEntity.Create("msg-1", "handler-a", fakeTime);

        // Act - Fail maxRetries times
        for (var i = 0; i < 3; i++)
        {
            status.MarkAsFailed(
                $"Error {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                fakeTime,
                maxRetries: 3,
                TimeSpan.FromMinutes(5)
            );
            fakeTime.Advance(TimeSpan.FromSeconds(1));
        }

        // Assert
        status.ErrorCount.Should().Be(3);
        status.IsPoisoned.Should().BeTrue();
        status.NextAttemptAt.Should().BeNull();
    }

    [Test]
    public void MarkAsFailed_JitterIsAlwaysWithinExpectedRange()
    {
        // Statistical test: run many iterations and verify all are within the jitter range.
        // With equal jitter, delay ∈ [base*0.5, base) for each attempt.
        var fakeTime = new FakeTimeProvider();

        for (var run = 0; run < 50; run++)
        {
            var status = InboxHandlerStatusEntity.Create(
                $"msg-{run.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                "handler-a",
                fakeTime
            );
            var now = fakeTime.GetUtcNow();

            status.MarkAsFailed("Error", fakeTime, maxRetries: 10, TimeSpan.FromMinutes(5));

            // ErrorCount=1: base = 2^1 = 2, delay ∈ [1, 2)
            status
                .NextAttemptAt!.Value.Should()
                .BeOnOrAfter(
                    now.AddSeconds(1),
                    $"run {run.ToString(System.Globalization.CultureInfo.InvariantCulture)}: jitter minimum is base*0.5 = 1s"
                );
            status
                .NextAttemptAt!.Value.Should()
                .BeOnOrBefore(
                    now.AddSeconds(2),
                    $"run {run.ToString(System.Globalization.CultureInfo.InvariantCulture)}: jitter maximum is base = 2s"
                );
        }
    }
}
