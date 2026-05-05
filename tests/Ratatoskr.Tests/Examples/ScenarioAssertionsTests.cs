using AwesomeAssertions;
using PlaygroundHost.Infrastructure.ScenarioRunning;

namespace Ratatoskr.Tests.Examples;

public sealed class ScenarioAssertionsTests
{
    [Test]
    public async Task WaitUntilAsync_ReturnsTrue_WhenPredicateTrueImmediately()
    {
        var time = TimeProvider.System;
        var result = await ScenarioAssertions.WaitUntilAsync(
            time,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            _ => Task.FromResult(true),
            CancellationToken.None);

        result.Should().BeTrue();
    }

    [Test]
    public async Task WaitUntilAsync_ReturnsFalse_WhenTimeoutElapses()
    {
        var time = TimeProvider.System;
        var result = await ScenarioAssertions.WaitUntilAsync(
            time,
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(40),
            _ => Task.FromResult(false),
            CancellationToken.None);

        result.Should().BeFalse();
    }

    [Test]
    public async Task WaitUntilAsync_ReturnsTrue_WhenPredicateTrueAfterOneDelay()
    {
        var time = TimeProvider.System;
        var n = 0;
        var result = await ScenarioAssertions.WaitUntilAsync(
            time,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(30),
            _ =>
            {
                n++;
                return Task.FromResult(n >= 2);
            },
            CancellationToken.None);

        result.Should().BeTrue();
    }
}
