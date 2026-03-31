using AwesomeAssertions;
using Ratatoskr.EfCore.Internal;
using TUnit.Core;

namespace Ratatoskr.Tests.EfCore;

public class BackoffCalculatorTests
{
    /// <summary>
    /// A Random subclass that returns a fixed value from NextDouble(), enabling exact boundary testing.
    /// </summary>
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    [Test]
    public void CalculateDelay_WithSeededRandom_ReturnsDeterministicResult()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);

        var delay1 = BackoffCalculator.CalculateDelay(3, maxRetryDelay, new Random(42));
        var delay2 = BackoffCalculator.CalculateDelay(3, maxRetryDelay, new Random(42));

        delay1.Should().Be(delay2);
    }

    [Test]
    public void CalculateDelay_WithIncreasingErrorCount_ReturnsExponentiallyGrowingDelay()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);
        var random = new FixedRandom(0.5);

        var delay1 = BackoffCalculator.CalculateDelay(1, maxRetryDelay, random);
        var delay2 = BackoffCalculator.CalculateDelay(2, maxRetryDelay, random);
        var delay3 = BackoffCalculator.CalculateDelay(3, maxRetryDelay, random);

        // base doubles each time: 2^1=2, 2^2=4, 2^3=8
        // With jitter=0.5: delay = base*0.5 + base*0.5*0.5 = base*0.75
        delay1.TotalSeconds.Should().Be(1.5);  // 2 * 0.75
        delay2.TotalSeconds.Should().Be(3.0);  // 4 * 0.75
        delay3.TotalSeconds.Should().Be(6.0);  // 8 * 0.75
    }

    [Test]
    public void CalculateDelay_WithErrorCountExceedingCap_ReturnsCappedDelay()
    {
        var maxRetryDelay = TimeSpan.FromSeconds(10);

        // errorCount=20 would produce 2^20 = 1,048,576 seconds without cap
        var delay = BackoffCalculator.CalculateDelay(20, maxRetryDelay, new FixedRandom(1.0));

        // With max jitter (1.0): delay = base*0.5 + base*0.5*1.0 = base = 10s (capped)
        delay.TotalSeconds.Should().Be(10.0);
    }

    [Test]
    public void CalculateDelay_WithZeroJitter_ReturnsExactlyHalfBase()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);

        // jitter=0.0: delay = base*0.5 + base*0.5*0.0 = base/2
        // For errorCount=1, base = 2^1 = 2s, so delay = 1.0s
        var delay = BackoffCalculator.CalculateDelay(1, maxRetryDelay, new FixedRandom(0.0));

        delay.TotalSeconds.Should().Be(1.0);
    }

    [Test]
    public void CalculateDelay_WithMaxJitter_ReturnsExactlyFullBase()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);

        // jitter=1.0: delay = base*0.5 + base*0.5*1.0 = base
        // For errorCount=1, base = 2^1 = 2s, so delay = 2.0s
        var delay = BackoffCalculator.CalculateDelay(1, maxRetryDelay, new FixedRandom(1.0));

        delay.TotalSeconds.Should().Be(2.0);
    }

    [Test]
    public void CalculateDelay_WithoutRandomParameter_UsesSharedRandom()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);

        // Should not throw — uses Random.Shared internally
        var delay = BackoffCalculator.CalculateDelay(1, maxRetryDelay);

        delay.TotalSeconds.Should().BeGreaterThan(0);
    }

    [Test]
    public void CalculateDelay_WithNegativeMaxRetryDelay_ReturnsNonNegativeDelay()
    {
        var negativeMax = TimeSpan.FromSeconds(-5);

        var delay = BackoffCalculator.CalculateDelay(3, negativeMax, new FixedRandom(0.5));

        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }
}
