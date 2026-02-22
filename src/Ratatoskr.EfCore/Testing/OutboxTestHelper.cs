using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Testing;

namespace Ratatoskr.EfCore.Testing;

/// <summary>
/// Test utilities for the EF Core outbox pattern.
/// Allows processing outbox messages synchronously in tests without the background processor.
/// </summary>
public static class OutboxTestHelper
{
    /// <summary>
    /// Processes all pending outbox messages synchronously using services from the given provider.
    /// This is the test equivalent of what the background <c>OutboxProcessor</c> does in production.
    /// Returns the total number of messages processed.
    /// </summary>
    /// <example>
    /// <code>
    /// // Arrange - save entity + outbox message
    /// dbContext.Orders.Add(order);
    /// dbContext.OutboxMessages.Add(new OrderCreated { OrderId = order.Id });
    /// await dbContext.SaveChangesAsync();
    ///
    /// // Act - process outbox
    /// var count = await OutboxTestHelper.ProcessAllAsync&lt;MyDbContext&gt;(serviceProvider);
    ///
    /// // Assert
    /// count.Should().Be(1);
    /// harness.Sent.ShouldContain&lt;OrderCreated&gt;();
    /// </code>
    /// </example>
    public static async Task<int> ProcessAllAsync<TDbContext>(IServiceProvider serviceProvider)
        where TDbContext : DbContext, IOutboxDbContext
    {
        var dbContext = serviceProvider.GetRequiredService<TDbContext>();
        var sender = serviceProvider.GetRequiredService<IMessageSender>();
        var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        var options = serviceProvider.GetRequiredService<IOptions<OutboxOptions>>();
        var logger = serviceProvider.GetService<ILogger<OutboxMessageProcessor<TDbContext>>>()
                     ?? NullLogger<OutboxMessageProcessor<TDbContext>>.Instance;

        var processor = new OutboxMessageProcessor<TDbContext>(
            dbContext, sender, timeProvider, options.Value, logger);

        var totalProcessed = 0;
        while (true)
        {
            var batchProcessed = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: false, CancellationToken.None);
            totalProcessed += batchProcessed;
            if (batchProcessed == 0) break;
        }

        return totalProcessed;
    }

    /// <summary>
    /// Processes all pending outbox messages using the harness's service provider.
    /// This is a convenience extension that delegates to <see cref="ProcessAllAsync{TDbContext}(IServiceProvider)"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var harness = factory.GetTestHarness();
    /// await harness.ProcessOutboxAsync&lt;MyDbContext&gt;();
    /// harness.Sent.ShouldContain&lt;OrderCreated&gt;();
    /// </code>
    /// </example>
    public static Task<int> ProcessOutboxAsync<TDbContext>(
        this RatatoskrTestHarness harness,
        CancellationToken cancellationToken = default)
        where TDbContext : DbContext, IOutboxDbContext
    {
        return ProcessAllAsync<TDbContext>(harness.ServiceProvider);
    }
}

/// <summary>
/// Extension methods for configuring the EF Core outbox for testing.
/// </summary>
public static class OutboxTestExtensions
{
    /// <summary>
    /// Registers the outbox pattern for testing without the background processor.
    /// Messages are not automatically processed - use <see cref="OutboxTestHelper.ProcessAllAsync{TDbContext}"/>
    /// to trigger processing explicitly.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddRatatoskr(bus =>
    /// {
    ///     bus.UseInMemory();
    ///     bus.AddEventPublishChannel("events", c => c.Produces&lt;OrderCreated&gt;());
    /// });
    /// services.AddTestOutbox&lt;MyDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddTestOutbox<TDbContext>(
        this IServiceCollection services,
        Action<OutboxBuilder<TDbContext>>? configure = null)
        where TDbContext : DbContext, IOutboxDbContext
    {
        var builder = new OutboxBuilder<TDbContext>(services);
        configure?.Invoke(builder);

        // Register options (without the background processor)
        services.AddSingleton(Options.Create(builder.Options));

        return services;
    }

    /// <summary>
    /// Registers the DbContext interceptor for outbox testing.
    /// This converts staged messages to entities on SaveChanges, but does NOT trigger background processing.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddDbContext&lt;MyDbContext&gt;((sp, options) =>
    /// {
    ///     options.UseSqlite("Data Source=:memory:");
    ///     options.RegisterTestOutbox&lt;MyDbContext&gt;(sp);
    /// });
    /// </code>
    /// </example>
    public static DbContextOptionsBuilder RegisterTestOutbox<TDbContext>(
        this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider)
        where TDbContext : DbContext, IOutboxDbContext
    {
        var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        var messageSerializer = serviceProvider.GetRequiredService<IMessageSerializer>();
        var enricher = serviceProvider.GetRequiredService<IMessagePropertiesEnricher>();
        var interceptor = new TestOutboxInterceptor<TDbContext>(messageSerializer, enricher, timeProvider);
        return builder.AddInterceptors(interceptor);
    }
}

/// <summary>
/// Outbox interceptor for tests that converts staged messages to entities
/// but does not trigger background processing.
/// </summary>
internal class TestOutboxInterceptor<TDbContext>(
    IMessageSerializer messageSerializer,
    IMessagePropertiesEnricher enricher,
    TimeProvider timeProvider)
    : OutboxTriggerInterceptor<TDbContext>(null!, messageSerializer, enricher, timeProvider)
    where TDbContext : DbContext, IOutboxDbContext
{
    public override ValueTask<int> SavedChangesAsync(
        Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        // Override base to prevent outbox processor triggering
        return ValueTask.FromResult(result);
    }
}
