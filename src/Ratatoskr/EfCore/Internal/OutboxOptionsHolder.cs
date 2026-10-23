using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Per-DbContext wrapper for <see cref="OutboxOptions"/>.
/// Allows multiple DbContext types to each have their own outbox configuration.
/// </summary>
// ReSharper disable once UnusedTypeParameter
internal class OutboxOptionsHolder<TDbContext>(OutboxOptions options)
    where TDbContext : DbContext
{
    public OutboxOptions Options => options;
}
