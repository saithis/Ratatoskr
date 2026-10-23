namespace Ratatoskr.EfCore;

/// <summary>
/// Interface that DbContext classes implement to support the inbox pattern.
/// Prefer implementing <see cref="IRatatoskrDbContext"/>.
/// </summary>
public interface IInboxDbContext : IRatatoskrDbContext;
