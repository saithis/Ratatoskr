using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Collapses the "look up a provider by context name and fail with a ProblemDetails
/// NotFound if it is missing or does not support the requested feature" dance that
/// every per-context endpoint would otherwise repeat verbatim.
/// </summary>
/// <remarks>
/// The helpers return <c>null</c> on success and a <see cref="ProblemHttpResult"/> on
/// failure so callers can early-return the error directly — the flow
/// <c>if (Resolver.EnsureOutbox(...) is { } error) return error;</c> keeps handler
/// methods short and stops diverging error copy from creeping back in.
/// </remarks>
internal static class ManagementDbContextResolver
{
    internal static ProblemHttpResult? EnsureContext(
        EfCoreManagementDbContextLookup lookup,
        string contextName,
        out DbContext dbContext
    )
    {
        var found = lookup.GetDbContext(contextName);
        if (found is null)
        {
            dbContext = null!;
            return ManagementResults.NotFound(
                $"No DbContext is registered under name '{contextName}'."
            );
        }

        dbContext = found;
        return null;
    }

    internal static ProblemHttpResult? EnsureOutbox(
        EfCoreManagementDbContextLookup lookup,
        string contextName,
        out DbContext dbContext
    )
    {
        if (EnsureContext(lookup, contextName, out dbContext) is { } error)
        {
            return error;
        }

        // Every management DbContext implements both interfaces (the AddEfCoreDurability
        // constraint requires it), so ask the descriptor which halves were actually configured.
        if (lookup.Find(contextName) is not { HasOutbox: true })
        {
            dbContext = null!;
            return ManagementResults.NotFound(
                $"No outbox is registered for DbContext '{contextName}'."
            );
        }
        return null;
    }

    internal static ProblemHttpResult? EnsureInbox(
        EfCoreManagementDbContextLookup lookup,
        string contextName,
        out DbContext dbContext
    )
    {
        if (EnsureContext(lookup, contextName, out dbContext) is { } error)
        {
            return error;
        }

        if (lookup.Find(contextName) is not { HasInbox: true })
        {
            dbContext = null!;
            return ManagementResults.NotFound(
                $"No inbox is registered for DbContext '{contextName}'."
            );
        }
        return null;
    }
}
