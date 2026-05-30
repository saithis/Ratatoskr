namespace Ratatoskr.TestHost;

/// <summary>
/// Assembly entry marker for MVC testing WebApplicationFactory so tests can target this app
/// without colliding with other Program types in the same test assembly.
/// </summary>
#pragma warning disable MA0036 // cannot be static: used as generic type argument for WebApplicationFactory<T>
public sealed class RatatoskrTestHostAppMarker;
#pragma warning restore MA0036
