namespace Ratatoskr.EfCore;

/// <summary>
/// Interface that DbContext classes must implement to support the inbox pattern.
/// The DbContext must also call <c>modelBuilder.AddInboxEntities()</c> in <c>OnModelCreating</c>.
/// </summary>
/// <example>
/// <code>
/// public class MyDbContext : DbContext, IInboxDbContext
/// {
///     // No additional properties required — inbox tables are configured via EF model builder.
///
///     protected override void OnModelCreating(ModelBuilder modelBuilder)
///     {
///         base.OnModelCreating(modelBuilder);
///         modelBuilder.AddInboxEntities();
///     }
/// }
/// </code>
/// </example>
public interface IInboxDbContext;
