using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Thin accessor over one registered DbContext for management API queries.
/// One implementation is registered per <c>AddEfCoreDurability&lt;TDbContext&gt;</c> call.
/// The <see cref="EfCoreManagementProviderLookup"/> resolves the correct provider by context name.
/// </summary>
internal interface IEfCoreManagementDbContextProvider
{
    string DbContextName { get; }
    bool HasOutbox { get; }
    bool HasInbox { get; }

    /// <summary>Returns the typed DbContext from the given service provider scope.</summary>
    DbContext GetDbContext(IServiceProvider serviceProvider);

    // ── Health data (read-only properties, no DB access) ─────────────────────

    EfCoreMetricsState MetricsState { get; }
    string MetricsContextKey { get; }
    DateTimeOffset? LastOutboxProcessingAt { get; }
    DateTimeOffset? LastInboxProcessingAt { get; }
}
