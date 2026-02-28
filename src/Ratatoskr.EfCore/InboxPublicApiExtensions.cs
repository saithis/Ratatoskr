using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Local;

namespace Ratatoskr.EfCore;

/// <summary>
/// Extension methods to enable the inbox pattern for durable, per-handler message delivery.
/// </summary>
public static class InboxPublicApiExtensions
{
    extension(RatatoskrBuilder builder)
    {
        /// <summary>
        /// Registers the inbox pattern with default options.
        /// </summary>
        /// <typeparam name="TDbContext">
        /// The application DbContext. Must implement <see cref="IInboxDbContext"/>,
        /// call <c>modelBuilder.AddInboxEntities()</c> in <c>OnModelCreating</c>,
        /// and be registered with <c>.RegisterInbox&lt;TDbContext&gt;(sp)</c> in the options builder.
        /// </typeparam>
        public RatatoskrBuilder UseEfCoreInbox<TDbContext>()
            where TDbContext : DbContext, IInboxDbContext
        {
            return builder.UseEfCoreInbox<TDbContext>(configure: null);
        }

        /// <summary>
        /// Registers the inbox pattern with custom options.
        /// <para>
        /// Call this <strong>after</strong> <c>UseLocalTransport()</c> if you use the local transport,
        /// so that the durable local sender can be registered in place of the regular one.
        /// </para>
        /// </summary>
        /// <typeparam name="TDbContext">
        /// The application DbContext. Must implement <see cref="IInboxDbContext"/>.
        /// </typeparam>
        public RatatoskrBuilder UseEfCoreInbox<TDbContext>(Action<InboxBuilder<TDbContext>>? configure)
            where TDbContext : DbContext, IInboxDbContext
        {
            var inboxBuilder = new InboxBuilder<TDbContext>();
            configure?.Invoke(inboxBuilder);

            builder.Services.AddSingleton(Options.Create(inboxBuilder.Options));
            builder.Services.AddSingleton<InboxProcessor<TDbContext>>();
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
            builder.Services.AddSingleton<IInboxInterceptor, InboxInterceptor<TDbContext>>();

            // If local transport is configured, replace LocalMessageSender with the durable version.
            // The durable sender writes to the inbox DB before the in-memory channel, ensuring
            // crash-safe delivery even when used with the outbox pattern.
            var localSenderDescriptor = builder.Services.FirstOrDefault(
                d => d.ServiceType == typeof(IMessageSender)
                  && d.ImplementationType == typeof(LocalMessageSender));

            if (localSenderDescriptor != null)
            {
                builder.Services.Remove(localSenderDescriptor);
                builder.Services.AddSingleton<IMessageSender, DurableLocalMessageSender<TDbContext>>();
            }

            return builder;
        }
    }

    /// <summary>
    /// Adds the interceptor that ties the inbox into the DbContext.
    /// Call this inside your <c>services.AddDbContext&lt;TDbContext&gt;((sp, c) => ...)</c> lambda.
    /// </summary>
    public static DbContextOptionsBuilder RegisterInbox<TDbContext>(
        this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider)
        where TDbContext : DbContext, IInboxDbContext
    {
        // No interceptor needed for inbox — the inbox writes are done directly, not via EF interceptors.
        // This method exists for API consistency with RegisterOutbox and future extensibility.
        return builder;
    }

    /// <summary>
    /// Adds the necessary inbox entities to the DB model.
    /// Call this inside <c>OnModelCreating</c> of your DbContext.
    /// </summary>
    public static void AddInboxEntities(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxMessageEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TransportName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SerializedProperties).IsRequired();
        });

        modelBuilder.Entity<InboxHandlerStatusEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Deduplication key: one row per (message, handler key)
            entity.HasIndex(e => new { e.MessageId, e.HandlerKey })
                .IsUnique()
                .HasDatabaseName("UX_InboxHandlerStatuses_MessageId_HandlerKey");

            // Index for the main processing query
            entity.HasIndex(
                e => new { e.CompletedAt, e.IsPoisoned, e.NextAttemptAt, e.ProcessingStartedAt, e.MessageId },
                "IX_InboxHandlerStatuses_Processing")
            .HasFilter("\"CompletedAt\" IS NULL AND \"IsPoisoned\" = false");

            entity.Property(e => e.HandlerKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);

            // FK to InboxMessages
            entity.HasOne<InboxMessageEntity>()
                .WithMany()
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
