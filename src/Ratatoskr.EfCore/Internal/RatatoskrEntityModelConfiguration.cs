using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// EF Core entity mappings for Ratatoskr durability. Used by <see cref="RatatoskrEfCoreModelExtensions.AddRatatoskrEfCoreModel"/>.
/// </summary>
internal static class RatatoskrEntityModelConfiguration
{
    internal static void ConfigureOutboxEntities(ModelBuilder modelBuilder, DatabaseFacade database)
    {
        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            var index = entity.HasIndex(
                e => new
                {
                    e.ProcessedAt,
                    e.IsPoisoned,
                    e.ScheduledAt,
                    e.NextAttemptAt,
                    e.ProcessingStartedAt,
                    e.CreatedAt,
                },
                "IX_OutboxMessages_Processing"
            );

            var filter = DatabaseProviderHelper.GetOutboxProcessingFilter(database);
            if (filter != null)
            {
                index.HasFilter(filter);
            }

            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SerializedProperties).IsRequired();
            entity.Property(e => e.TransportName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.Property(e => e.RequeuedCount).HasDefaultValue(0);
        });
    }

    internal static void ConfigureManagementEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagementOperationEntity>(entity =>
        {
            entity.HasKey(e => e.OperationId);

            // The caller supplies the key, so EF must not try to generate one — a duplicate
            // delivery has to collide on insert, which is the entire idempotency mechanism.
            entity.Property(e => e.OperationId).ValueGeneratedNever();

            entity.Property(e => e.Operation).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Actor).HasMaxLength(400);

            // Retention sweeps by completion time, so the cleanup worker deletes in bounded,
            // index-backed batches rather than scanning a growing table every hour.
            entity.HasIndex(e => e.CompletedAt, "IX_ManagementOperations_CompletedAt");
        });
    }

    internal static void ConfigureInboxEntities(ModelBuilder modelBuilder, DatabaseFacade database)
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

            entity
                .HasIndex(e => new { e.MessageId, e.HandlerKey })
                .IsUnique()
                .HasDatabaseName("UX_InboxHandlerStatuses_MessageId_HandlerKey");

            var processingIndex = entity.HasIndex(
                e => new
                {
                    e.CompletedAt,
                    e.IsPoisoned,
                    e.NextAttemptAt,
                    e.ProcessingStartedAt,
                    e.MessageId,
                },
                "IX_InboxHandlerStatuses_Processing"
            );

            var filter = DatabaseProviderHelper.GetInboxProcessingFilter(database);
            if (filter != null)
            {
                processingIndex.HasFilter(filter);
            }

            entity.Property(e => e.HandlerKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.Property(e => e.RequeuedCount).HasDefaultValue(0);

            entity
                .HasOne<InboxMessageEntity>()
                .WithMany()
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
