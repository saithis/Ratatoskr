using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;

namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public static class ScenarioAssertions
{
    /// <summary>
    /// Polls until <paramref name="predicateAsync"/> returns true or <paramref name="timeout"/> elapses.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        TimeProvider time,
        TimeSpan timeout,
        TimeSpan pollInterval,
        Func<CancellationToken, Task<bool>> predicateAsync,
        CancellationToken cancellationToken
    )
    {
        var deadline = time.GetUtcNow() + timeout;
        while (time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await predicateAsync(cancellationToken))
                return true;

            await Task.Delay(pollInterval, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Polls an integer metric until it is strictly greater than <paramref name="baselineExclusive"/>.</summary>
    public static async Task<ScenarioVerdict> IntMetricEventuallyExceedsBaselineAsync(
        TimeProvider time,
        TimeSpan timeout,
        TimeSpan pollInterval,
        int baselineExclusive,
        Func<CancellationToken, Task<int>> readCountAsync,
        string metricDescriptionForFailure,
        CancellationToken cancellationToken
    )
    {
        int? capturedAfter = null;
        var ok = await WaitUntilAsync(
            time,
            timeout,
            pollInterval,
            async ct =>
            {
                var v = await readCountAsync(ct);
                if (v > baselineExclusive)
                {
                    capturedAfter = v;
                    return true;
                }

                return false;
            },
            cancellationToken
        );

        if (ok)
            return new ScenarioVerdict(
                true,
                details: new { before = baselineExclusive, after = capturedAfter!.Value }
            );

        var final = await readCountAsync(cancellationToken);
        return new ScenarioVerdict(
            false,
            $"{metricDescriptionForFailure} did not increase within timeout (before={baselineExclusive}, after={final})."
        );
    }

    /// <summary>
    /// Polls RabbitMQ DLQ depth for <paramref name="mainQueueName"/> until it exceeds <paramref name="baselineExclusive"/>.</summary>
    public static async Task<ScenarioVerdict> DlqDepthEventuallyExceedsBaselineAsync(
        string rabbitConnectionString,
        string mainQueueName,
        uint baselineExclusive,
        TimeProvider time,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken
    )
    {
        uint? capturedAfter = null;
        var ok = await WaitUntilAsync(
            time,
            timeout,
            pollInterval,
            async ct =>
            {
                var d = await RabbitDlqDepthReader.GetDlqCountAsync(
                    rabbitConnectionString,
                    mainQueueName,
                    ct
                );
                if (d > baselineExclusive)
                {
                    capturedAfter = d;
                    return true;
                }

                return false;
            },
            cancellationToken
        );

        if (ok)
            return new ScenarioVerdict(
                true,
                details: new { before = baselineExclusive, after = capturedAfter!.Value }
            );

        var final = await RabbitDlqDepthReader.GetDlqCountAsync(
            rabbitConnectionString,
            mainQueueName,
            cancellationToken
        );
        return new ScenarioVerdict(
            false,
            $"DLQ depth did not increase within timeout (before={baselineExclusive}, after={final})."
        );
    }

    public static async Task<ScenarioVerdict> OrderEventuallyAsync(
        IServiceScopeFactory scopeFactory,
        Guid orderId,
        OrderStatus expected,
        TimeSpan timeout,
        TimeProvider time,
        CancellationToken cancellationToken
    )
    {
        var deadline = time.GetUtcNow() + timeout;
        while (time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PublisherDbContext>();
            var order = await db
                .Orders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order is { Status: var s } && s == expected)
                return new ScenarioVerdict(true);

            await Task.Delay(ScenarioTiming.OrderPollInterval, cancellationToken);
        }

        await using var scope2 = scopeFactory.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PublisherDbContext>();
        var final = await db2
            .Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        return new ScenarioVerdict(
            false,
            $"Order {orderId} did not reach {expected} within {timeout}. Current: {final?.Status.ToString() ?? "missing"}."
        );
    }
}
