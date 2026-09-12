using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.UI.Store;

/// <summary>
/// The last announcement received from one replica, on one transport.
/// </summary>
/// <remarks>
/// Persisting this is what lets a restarted dashboard show the fleet immediately instead of an
/// empty page until the next heartbeat — which, for a service on a fifteen-second interval during
/// an incident, is fifteen seconds of the operator wondering whether everything is down.
/// </remarks>
public sealed class DashboardServiceSnapshot
{
    /// <summary>The transport the announcement arrived on.</summary>
    public string TransportName { get; set; } = string.Empty;

    /// <summary>The logical service name.</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>The replica's identity.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>When this dashboard first heard from the replica.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When this dashboard last heard from it.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>The announcement's own timestamp, used to reject out-of-order deliveries.</summary>
    public DateTimeOffset AnnouncedAt { get; set; }

    /// <summary>The full announcement, as it arrived.</summary>
    public string AnnouncementJson { get; set; } = string.Empty;
}

/// <summary>One management mutation, and what came of it.</summary>
public sealed class DashboardAuditEntry
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The operation id sent to the service, so an entry can be matched to its record there.</summary>
    public Guid OperationId { get; set; }

    /// <summary>The transport the request went out on.</summary>
    public string TransportName { get; set; } = string.Empty;

    /// <summary>The service that was asked.</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>The replica, when the request was instance-targeted.</summary>
    public string? InstanceId { get; set; }

    /// <summary>The DbContext the operation applied to.</summary>
    public string? Resource { get; set; }

    /// <summary>The operation name.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Who asked.</summary>
    public string? Actor { get; set; }

    /// <summary>Who asked, as a human would read it.</summary>
    public string? ActorDisplayName { get; set; }

    /// <summary>The request body: the filter, or the id list.</summary>
    public string? RequestJson { get; set; }

    /// <summary>When the dashboard sent the request.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When it got an answer.</summary>
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>The result status.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The stable error code, when the operation failed.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>The response body, when the operation succeeded.</summary>
    public string? ResultJson { get; set; }
}

/// <summary>
/// The dashboard's own state: what it has seen, and what it has done.
/// </summary>
/// <remarks>
/// Unlike the inbox and outbox model — which is embedded in the application's own context —
/// this context belongs to Ratatoskr, so it ships migrations and you apply them.
/// <para>
/// Do not use <c>EnsureCreated</c> on it. That helper is all-or-nothing per database and silently
/// creates nothing when the database already exists, which is precisely the common case of
/// pointing the dashboard at an existing application database. Table names are prefixed rather
/// than placed in their own schema because SQLite has no schemas at all, and a supported
/// single-node setup should not need a different migration path from a Postgres one.
/// </para>
/// </remarks>
public sealed class RatatoskrDashboardDbContext(DbContextOptions<RatatoskrDashboardDbContext> options)
    : DbContext(options)
{
    /// <summary>The last announcement from each replica, per transport.</summary>
    public DbSet<DashboardServiceSnapshot> ServiceSnapshots => Set<DashboardServiceSnapshot>();

    /// <summary>Every mutation the dashboard has issued.</summary>
    public DbSet<DashboardAuditEntry> AuditEntries => Set<DashboardAuditEntry>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DashboardServiceSnapshot>(entity =>
        {
            entity.ToTable("RatatoskrDashboardServices");

            // The natural key is the replica's identity across all three dimensions; a surrogate
            // would only add a uniqueness constraint we would then have to maintain anyway.
            entity.HasKey(e => new
            {
                e.TransportName,
                e.ServiceName,
                e.InstanceId,
            });

            entity.Property(e => e.TransportName).HasMaxLength(100);
            entity.Property(e => e.ServiceName).HasMaxLength(200);
            entity.Property(e => e.InstanceId).HasMaxLength(200);
            entity.Property(e => e.AnnouncementJson).IsRequired();
            entity.HasIndex(e => e.LastSeenAt, "IX_RatatoskrDashboardServices_LastSeenAt");
        });

        modelBuilder.Entity<DashboardAuditEntry>(entity =>
        {
            entity.ToTable("RatatoskrDashboardAudit");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.TransportName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ServiceName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.InstanceId).HasMaxLength(200);
            entity.Property(e => e.Resource).HasMaxLength(200);
            entity.Property(e => e.Operation).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Actor).HasMaxLength(400);
            entity.Property(e => e.ActorDisplayName).HasMaxLength(400);
            entity.Property(e => e.Outcome).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ErrorCode).HasMaxLength(100);

            // The audit view reads newest-first, and retention deletes oldest-first; one
            // descending index on the start time serves both.
            entity.HasIndex(e => e.StartedAt, "IX_RatatoskrDashboardAudit_StartedAt");
            entity.HasIndex(e => e.OperationId, "IX_RatatoskrDashboardAudit_OperationId");
        });
    }
}
