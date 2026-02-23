using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Contains the extension methods to enable/configure the outbox
/// </summary>
public static class PublicApiExtensions
{
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
            var outboxBuilder = new OutboxBuilder<TDbContext>(builder.Services);
            configure?.Invoke(outboxBuilder);
        
            // Register options
            builder.Services.AddSingleton(Options.Create(outboxBuilder.Options));
        
            builder.Services.AddSingleton<OutboxProcessor<TDbContext>>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxProcessor<TDbContext>>());
        
            return builder;
        }

        /// <summary>
        /// Registers the outbox pattern with options from configuration.
        /// </summary>
        public RatatoskrBuilder AddEfCoreOutbox<TDbContext>(IConfiguration configuration)
            where TDbContext : DbContext, IOutboxDbContext
        {
            builder.Services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        
            builder.Services.AddSingleton<OutboxProcessor<TDbContext>>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxProcessor<TDbContext>>());
        
            return builder;
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
        var outboxProcessor = serviceProvider.GetRequiredService<OutboxProcessor<TDbContext>>();
        var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        var messageSerializer = serviceProvider.GetRequiredService<IMessageSerializer>();
        var enricher = serviceProvider.GetRequiredService<IMessagePropertiesEnricher>();
        var observers = serviceProvider.GetServices<IMessageActivityObserver>();
        var interceptor = new OutboxTriggerInterceptor<TDbContext>(outboxProcessor, messageSerializer, enricher, observers, timeProvider);
        return builder.AddInterceptors(interceptor);
    }
    
    /// <summary>
    /// Adds the necessary outbox entities to the DB model.
    /// </summary>
    public static void AddOutboxEntities(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            // Primary key (if not already configured by convention)
            entity.HasKey(e => e.Id);
            
            // Index for the main query: unprocessed, not poisoned, ready to process
            // Covers: ProcessedAt, IsPoisoned, NextAttemptAt, ProcessingStartedAt, CreatedAt
            entity.HasIndex(
                e => new { 
                    e.ProcessedAt, 
                    e.IsPoisoned, 
                    e.NextAttemptAt, 
                    e.ProcessingStartedAt, 
                    e.CreatedAt 
                },
                "IX_OutboxMessages_Processing")
            .HasFilter("\"ProcessedAt\" IS NULL AND \"IsPoisoned\" = false");
            
            // Configure column constraints
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SerializedProperties).IsRequired();
        });
    }
}