using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Config;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Extension methods to enable the inbox pattern for durable, per-handler message delivery.
/// </summary>
public static class InboxPublicApiExtensions
{
    /// <summary>
    /// Enables the inbox pattern on this consume channel.
    /// All handlers registered with a stable key on this channel will be inbox-managed.
    /// Requires <c>AddEfCoreDurability&lt;TDbContext&gt;(d =&gt; d.UseInbox())</c> to be called on the bus builder.
    /// </summary>
    public static ConsumeChannelBuilder UseInbox<TDbContext>(this ConsumeChannelBuilder builder)
        where TDbContext : DbContext, IInboxDbContext
    {
        builder.Channel.SetExtension(new ChannelInboxConfig(typeof(TDbContext)));

        // Deferred validation: ensure AddEfCoreDurability<TDbContext>(d => d.UseInbox()) was called
        var services = builder.Services;
        var channelName = builder.Channel.ChannelName;
        builder.RatatoskrBuilder.AddValidator(_ =>
        {
            if (!services.Any(d => d.ServiceType == typeof(InboxOptionsHolder<TDbContext>)))
                throw new InvalidOperationException(
                    $"Channel '{channelName}' uses UseInbox<{typeof(TDbContext).Name}>() " +
                    $"but AddEfCoreDurability<{typeof(TDbContext).Name}>(d => d.UseInbox()) was not configured. " +
                    $"Call AddEfCoreDurability before configuring consume channels.");
        });

        return builder;
    }

    /// <summary>
    /// Adds the necessary inbox entities to the DB model.
    /// Call this inside <c>OnModelCreating</c> of your DbContext.
    /// Pass <see cref="DbContext.Database"/> so a partial/filtered index can be applied for supported
    /// providers (PostgreSQL, SQL Server). Omitting it previously caused full-table indexes and severe
    /// performance issues on large inbox handler status tables.
    /// </summary>
    public static void AddInboxEntities(this ModelBuilder modelBuilder, DatabaseFacade database)
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

            entity.HasIndex(e => new { e.MessageId, e.HandlerKey })
                .IsUnique()
                .HasDatabaseName("UX_InboxHandlerStatuses_MessageId_HandlerKey");

            var processingIndex = entity.HasIndex(
                e => new { e.CompletedAt, e.IsPoisoned, e.NextAttemptAt, e.ProcessingStartedAt, e.MessageId },
                "IX_InboxHandlerStatuses_Processing");

            var filter = DatabaseProviderHelper.GetInboxProcessingFilter(database);
            if (filter != null)
                processingIndex.HasFilter(filter);

            entity.Property(e => e.HandlerKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.Version).IsConcurrencyToken();

            entity.HasOne<InboxMessageEntity>()
                .WithMany()
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
