using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// A row that a processor claims before working on it. Claiming bumps the optimistic
/// concurrency token, so only one worker can successfully claim the same row.
/// </summary>
internal interface IClaimableEntity
{
    Guid Id { get; }

    void MarkAsProcessing(TimeProvider timeProvider);
}

/// <summary>
/// Optimistic-concurrency helpers shared by the inbox and outbox processors.
/// </summary>
internal static class ProcessingConcurrency
{
    /// <summary>
    /// Marks the entities as processing and saves them. Entities that were concurrently modified
    /// (for example claimed by another worker) are dropped from the claim set and the remaining
    /// ones are saved again. Returns the entities that were successfully claimed (possibly empty).
    /// </summary>
    public static async Task<TEntity[]> ClaimAsync<TEntity>(
        DbContext dbContext,
        TEntity[] entities,
        TimeProvider timeProvider,
        Action<int> onConflictsSkipped,
        CancellationToken cancellationToken
    )
        where TEntity : class, IClaimableEntity
    {
        foreach (var entity in entities)
        {
            entity.MarkAsProcessing(timeProvider);
        }

        while (true)
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return entities;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                var conflictIds = await ReloadConflictsAsync<TEntity>(ex, cancellationToken);
                onConflictsSkipped(conflictIds.Count);

                var remaining = entities.Where(e => !conflictIds.Contains(e.Id)).ToArray();
                if (remaining.Length == entities.Length)
                {
                    // The conflict did not identify any entity in the current claim set (for
                    // example a DbUpdateConcurrencyException with an empty Entries collection).
                    // Retrying the same unchanged set would spin forever, so rethrow and let the
                    // batch be retried fresh on the next polling cycle.
                    throw;
                }

                entities = remaining;
                if (entities.Length == 0)
                {
                    return entities;
                }
            }
        }
    }

    /// <summary>
    /// Saves the pending changes after <paramref name="current"/> was processed. On a concurrency
    /// conflict the conflicting rows are reloaded; if <paramref name="current"/> was one of them it
    /// is skipped and <c>false</c> is returned, otherwise the remaining changes are saved again.
    /// Always uses <see cref="CancellationToken.None"/> so the outcome of work that already
    /// happened is persisted even during shutdown.
    /// </summary>
    public static async Task<bool> SaveSkippingConflictsAsync<TEntity>(
        DbContext dbContext,
        TEntity current,
        Action<int> onConflictsSkipped
    )
        where TEntity : class, IClaimableEntity
    {
        try
        {
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var conflictIds = await ReloadConflictsAsync<TEntity>(ex, CancellationToken.None);
            onConflictsSkipped(conflictIds.Count);

            if (conflictIds.Contains(current.Id))
            {
                return false;
            }

            await dbContext.SaveChangesAsync(CancellationToken.None);
            return true;
        }
    }

    private static async Task<HashSet<Guid>> ReloadConflictsAsync<TEntity>(
        DbUpdateConcurrencyException ex,
        CancellationToken cancellationToken
    )
        where TEntity : class, IClaimableEntity
    {
        var conflictIds = new HashSet<Guid>();
        foreach (var entry in ex.Entries)
        {
            conflictIds.Add(((TEntity)entry.Entity).Id);
            await entry.ReloadAsync(cancellationToken);
        }

        return conflictIds;
    }
}
