using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Per-DbContext wrapper for <see cref="InboxOptions"/>.
/// Allows multiple DbContext types to each have their own inbox configuration.
/// </summary>
// ReSharper disable once UnusedTypeParameter
internal class InboxOptionsHolder<TDbContext>(InboxOptions options)
    where TDbContext : DbContext
{
    public InboxOptions Options => options;
}
