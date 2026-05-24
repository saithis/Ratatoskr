using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Thin accessor over one registered DbContext for management API queries.
/// One implementation is registered per <c>AddEfCoreDurability&lt;TDbContext&gt;</c> call.
/// The <see cref="EfCoreManagementDbContextLookup"/> resolves the correct provider by context name.
/// </summary>
internal interface IEfCoreManagementDbContextDescriptor
{
    public string DbContextName { get; }
    public string DbContextFullName { get; }
    public Type DbContextType { get; }
    public bool HasOutbox { get; }
    public bool HasInbox { get; }

    /// <summary>Returns the typed DbContext from the given service provider scope.</summary>
    public DbContext GetDbContext(IServiceProvider serviceProvider);

    // ── Health data (read-only properties, no DB access) ─────────────────────

    public DateTimeOffset? LastOutboxProcessingAt { get; }
    public DateTimeOffset? LastInboxProcessingAt { get; }
}
