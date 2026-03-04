using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Contains the extension methods to enable/configure the outbox
/// </summary>
public static class PublicApiExtensions
{
    private class OutboxBuildTimeState
    {
        public OutboxOptionsRegistry OptionsRegistry { get; } = new();
    }

    private static OutboxBuildTimeState GetOrCreateState(RatatoskrBuilder builder) =>
        builder.GetOrSetExtension(() => new OutboxBuildTimeState());

    extension(RatatoskrBuilder builder)
    {
        /// <summary>
        /// Registers the outbox pattern with default options.
        /// </summary>
        public RatatoskrBuilder AddEfCoreOutbox<TDbContext>()
            where TDbContext : DbContext, IOutboxDbContext
        {
            return builder.AddEfCoreOutbox<TDbContext>(configure: null);
        }

        /// <summary>
        /// Registers the outbox pattern with custom options via builder.
        /// </summary>
        public RatatoskrBuilder AddEfCoreOutbox<TDbContext>(Action<OutboxBuilder<TDbContext>>? configure)
            where TDbContext : DbContext, IOutboxDbContext
        {
            var state = GetOrCreateState(builder);

            if (state.OptionsRegistry.Contains(typeof(TDbContext)))
                throw new InvalidOperationException(
                    $"AddEfCoreOutbox<{typeof(TDbContext).Name}>() has already been called. " +
                    $"Each DbContext type can only be registered once for the outbox.");

            var outboxBuilder = new OutboxBuilder<TDbContext>();
            configure?.Invoke(outboxBuilder);

            // Auto-assign per-DbContext lock name if still default
            if (outboxBuilder.Options.LockName == "OutboxProcessor")
                outboxBuilder.Options.LockName = $"OutboxProcessor-{typeof(TDbContext).Name}";

            state.OptionsRegistry.Register(typeof(TDbContext), outboxBuilder.Options);
            RegisterOutboxServices<TDbContext>(builder, state, outboxBuilder.Options);

            return builder;
        }

        /// <summary>
        /// Registers the outbox pattern with options from configuration.
        /// </summary>
        public RatatoskrBuilder AddEfCoreOutbox<TDbContext>(IConfiguration configuration)
            where TDbContext : DbContext, IOutboxDbContext
        {
            var state = GetOrCreateState(builder);

            if (state.OptionsRegistry.Contains(typeof(TDbContext)))
                throw new InvalidOperationException(
                    $"AddEfCoreOutbox<{typeof(TDbContext).Name}>() has already been called. " +
                    $"Each DbContext type can only be registered once for the outbox.");

            var options = new OutboxOptions { LockName = $"OutboxProcessor-{typeof(TDbContext).Name}" };
            configuration.GetSection(OutboxOptions.SectionName).Bind(options);

            state.OptionsRegistry.Register(typeof(TDbContext), options);
            RegisterOutboxServices<TDbContext>(builder, state, options);

            return builder;
        }
    }

    private static void RegisterOutboxServices<TDbContext>(
        RatatoskrBuilder builder, OutboxBuildTimeState state, OutboxOptions options)
        where TDbContext : DbContext, IOutboxDbContext
    {
        // Register shared singletons (idempotent)
        builder.Services.TryAddSingleton(state.OptionsRegistry);
        builder.Services.TryAddSingleton<OutboxTelemetry>();

        // Register per-DbContext services
        builder.Services.AddSingleton<OutboxTriggerInterceptor<TDbContext>>();
        builder.Services.AddTransient<OutboxMessageProcessor<TDbContext>>();
        builder.Services.AddSingleton<OutboxProcessor<TDbContext>>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxProcessor<TDbContext>>());

        // Register cleanup processor if any retention is configured
        if (options.CompletedRetention != null || options.PoisonedRetention != null)
        {
            builder.Services.AddSingleton<OutboxCleanupProcessor<TDbContext>>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxCleanupProcessor<TDbContext>>());
        }
    }

    /// <summary>
    /// Registers the DbContext interceptor that is responsible for converting the messages to ef core entities for saving and triggering the outbox processor afterward for faster dispatch to the broker.
    /// </summary>
    /// <param name="serviceProvider">ServiceProvider that you get from the services.AddDbContext&lt;TDbContext&gt;((sp, c) => ..) call.</param>
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
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="database">
    /// The <see cref="DatabaseFacade"/> from your DbContext (<c>this.Database</c> in <c>OnModelCreating</c>).
    /// Pass this to enable provider-specific partial indexes.
    /// </param>
    public static void AddOutboxEntities(this ModelBuilder modelBuilder, DatabaseFacade? database)
    {
        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            // Primary key (if not already configured by convention)
            entity.HasKey(e => e.Id);

            // Index for the main query: unprocessed, not poisoned, ready to process.
            var index = entity.HasIndex(
                e => new {
                    e.ProcessedAt,
                    e.IsPoisoned,
                    e.NextAttemptAt,
                    e.ProcessingStartedAt,
                    e.CreatedAt
                },
                "IX_OutboxMessages_Processing");

            // Apply a partial/filtered index for supported providers.
            // This dramatically improves query performance on large tables by excluding processed rows.
            var filter = DatabaseProviderHelper.GetOutboxProcessingFilter(database);
            if (filter != null)
                index.HasFilter(filter);

            // Configure column constraints
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SerializedProperties).IsRequired();
            entity.Property(e => e.TransportName).HasMaxLength(50).IsRequired();
        });
    }
}
