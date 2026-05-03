using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;

namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public static class ScenarioAssertions
{
    public static async Task<ScenarioVerdict> OrderEventuallyAsync(
        IServiceScopeFactory scopeFactory,
        Guid orderId,
        OrderStatus expected,
        TimeSpan timeout,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PublisherDbContext>();
            var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order is { Status: var s } && s == expected)
                return new ScenarioVerdict(true);

            await Task.Delay(500, cancellationToken);
        }

        await using var scope2 = scopeFactory.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PublisherDbContext>();
        var final = await db2.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        return new ScenarioVerdict(
            false,
            $"Order {orderId} did not reach {expected} within {timeout}. Current: {final?.Status.ToString() ?? "missing"}.");
    }
}
