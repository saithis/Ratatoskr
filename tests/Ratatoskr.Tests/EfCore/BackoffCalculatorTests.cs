using AwesomeAssertions;
using Ratatoskr.EfCore.Internal;
using TUnit.Core;

namespace Ratatoskr.Tests.EfCore;

public class BackoffCalculatorTests
{
    [Test]
    public void CalculateDelay_WithSeededRandom_IsDeterministic()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);

        var delay1 = BackoffCalculator.CalculateDelay(3, maxRetryDelay, new Random(42));
        var delay2 = BackoffCalculator.CalculateDelay(3, maxRetryDelay, new Random(42));

        delay1.Should().Be(delay2);
    }

    [Test]
    public void CalculateDelay_DelayGrowsExponentially()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);
        var random = new Random(42);

        var delay1 = BackoffCalculator.CalculateDelay(1, maxRetryDelay, new Random(42));
        var delay2 = BackoffCalculator.CalculateDelay(2, maxRetryDelay, new Random(42));
        var delay3 = BackoffCalculator.CalculateDelay(3, maxRetryDelay, new Random(42));

        // Each successive delay should be larger (base doubles: 2^1, 2^2, 2^3)
        delay2.Should().BeGreaterThan(delay1);
        delay3.Should().BeGreaterThan(delay2);
    }

    [Test]
    public void CalculateDelay_IsCappedAtMaxRetryDelay()
    {
        var maxRetryDelay = TimeSpan.FromSeconds(10);

        // errorCount=20 would produce 2^20 = 1,048,576 seconds without cap
        var delay = BackoffCalculator.CalculateDelay(20, maxRetryDelay, new Random(42));

        delay.Should().BeLessThanOrEqualTo(maxRetryDelay);
    }

    [Test]
    public void CalculateDelay_MinimumIsHalfOfBaseDelay()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);

        // With jitter formula: base/2 + random(0, base/2), minimum is base/2
        // For errorCount=1, base = 2^1 = 2s, so minimum is 1s
        // Use a Random that returns 0.0 for NextDouble (minimum jitter)
        // We can't easily control Random.NextDouble to return exactly 0,
        // but we can verify the delay is at least base/2
        var delay = BackoffCalculator.CalculateDelay(1, maxRetryDelay, new Random(42));

        // base = 2^1 = 2, minimum = base/2 = 1.0s
        delay.TotalSeconds.Should().BeGreaterThanOrEqualTo(1.0);
        // maximum = base = 2.0s
        delay.TotalSeconds.Should().BeLessThanOrEqualTo(2.0);
    }

    [Test]
    public void CalculateDelay_WithoutRandom_UsesSharedRandom()
    {
        var maxRetryDelay = TimeSpan.FromMinutes(5);

        // Should not throw — uses Random.Shared internally
        var delay = BackoffCalculator.CalculateDelay(1, maxRetryDelay);

        delay.TotalSeconds.Should().BeGreaterThan(0);
    }
}
