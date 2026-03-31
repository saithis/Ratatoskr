using AwesomeAssertions;
using Ratatoskr.EfCore.Internal;
using TUnit.Core;

namespace Ratatoskr.Tests.EfCore;

public class BackoffCalculatorTests
{
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

        var delay1 = BackoffCalculator.CalculateDelay(1, maxRetryDelay, new Random(42));
        var delay2 = BackoffCalculator.CalculateDelay(2, maxRetryDelay, new Random(42));
        var delay3 = BackoffCalculator.CalculateDelay(3, maxRetryDelay, new Random(42));

        // Each successive delay should be larger (base doubles: 2^1, 2^2, 2^3)
        delay2.Should().BeGreaterThan(delay1);
        delay3.Should().BeGreaterThan(delay2);
    }

    [Test]
    public void CalculateDelay_WithErrorCountExceedingCap_ReturnsCappedDelay()
    {
        var maxRetryDelay = TimeSpan.FromSeconds(10);

        // errorCount=20 would produce 2^20 = 1,048,576 seconds without cap
        var delay = BackoffCalculator.CalculateDelay(20, maxRetryDelay, new Random(42));

        delay.Should().BeLessThanOrEqualTo(maxRetryDelay);
    }

    [Test]
    public void CalculateDelay_WithEqualJitter_ReturnsDelayBetweenHalfAndFullBase()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);

        // With jitter formula: base/2 + random(0, base/2), delay is in [base/2, base]
        // For errorCount=1, base = 2^1 = 2s, so delay is in [1.0s, 2.0s]
        var delay = BackoffCalculator.CalculateDelay(1, maxRetryDelay, new Random(42));

        // base = 2^1 = 2, minimum = base/2 = 1.0s, maximum = base = 2.0s
        delay.TotalSeconds.Should().BeGreaterThanOrEqualTo(1.0);
        delay.TotalSeconds.Should().BeLessThanOrEqualTo(2.0);
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

        var delay = BackoffCalculator.CalculateDelay(3, negativeMax, new Random(42));

        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }
}
