using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Contains the extension methods to enable/configure the outbox.
/// </summary>
public static class PublicApiExtensions
{
    extension(RatatoskrBuilder builder)
    {
        /// <summary>
        /// Registers the outbox pattern with default options.
        /// Can be called multiple times with different DbContext types.
        /// </summary>
        public RatatoskrBuilder AddEfCoreOutbox<TDbContext>()
            where TDbContext : DbContext, IOutboxDbContext
        {
            return builder.AddEfCoreOutbox<TDbContext>(configure: null);
        }

        /// <summary>
        /// Registers the outbox pattern with custom options via builder.
        /// Can be called multiple times with different DbContext types.
        /// </summary>
        public RatatoskrBuilder AddEfCoreOutbox<TDbContext>(Action<OutboxBuilder<TDbContext>>? configure)
            where TDbContext : DbContext, IOutboxDbContext
        {
            // Skip if already registered for this DbContext type
            if (builder.Services.Any(d => d.ServiceType == typeof(OutboxOptionsHolder<TDbContext>)))
                return builder;

            var outboxBuilder = new OutboxBuilder<TDbContext>(builder.Services);
            configure?.Invoke(outboxBuilder);

            // Default lock name includes DbContext type to avoid collisions across different DbContexts
            if (outboxBuilder.Options.LockName == OutboxOptions.DefaultLockName)
                outboxBuilder.Options.LockName = $"OutboxProcessor_{typeof(TDbContext).Name}";

            builder.Services.AddSingleton(new OutboxOptionsHolder<TDbContext>(outboxBuilder.Options));
            builder.Services.AddSingleton<OutboxTelemetry>();
            builder.Services.AddSingleton<OutboxTriggerInterceptor<TDbContext>>();
            builder.Services.AddTransient<OutboxMessageProcessor<TDbContext>>();
            builder.Services.AddSingleton<OutboxProcessor<TDbContext>>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxProcessor<TDbContext>>());

            return builder;
        }
    }

    /// <summary>
    /// Registers the DbContext interceptor that is responsible for converting the messages to ef core entities for saving and triggering the outbox processor afterward for faster dispatch to the broker.
    /// </summary>
    public static DbContextOptionsBuilder RegisterOutbox<TDbContext>(this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider)
        where TDbContext : DbContext, IOutboxDbContext
    {
        var interceptor = serviceProvider.GetRequiredService<OutboxTriggerInterceptor<TDbContext>>();
        return builder.AddInterceptors(interceptor);
    }

    /// <summary>
    /// Adds the necessary outbox entities to the DB model.
    /// </summary>
    public static void AddOutboxEntities(this ModelBuilder modelBuilder) =>
        modelBuilder.AddOutboxEntities(database: null);

    /// <summary>
    /// Adds the necessary outbox entities to the DB model.
    /// When <paramref name="database"/> is provided, a partial/filtered index is applied
    /// for supported providers (PostgreSQL, SQL Server) to improve query performance on large tables.
    /// </summary>
    public static void AddOutboxEntities(this ModelBuilder modelBuilder, DatabaseFacade? database)
    {
        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            var index = entity.HasIndex(
                e => new {
                    e.ProcessedAt,
                    e.IsPoisoned,
                    e.NextAttemptAt,
                    e.ProcessingStartedAt,
                    e.CreatedAt
                },
                "IX_OutboxMessages_Processing");

            var filter = DatabaseProviderHelper.GetOutboxProcessingFilter(database);
            if (filter != null)
                index.HasFilter(filter);

            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SerializedProperties).IsRequired();
            entity.Property(e => e.TransportName).HasMaxLength(50).IsRequired();
        });
    }
}
