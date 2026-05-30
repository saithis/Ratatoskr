namespace Ratatoskr.Endpoints;

/// <summary>
/// Marker feature set only by in-process proxy dispatch. Cannot be spoofed via HTTP,
/// and internal so that third-party code cannot attach a custom implementation to an
/// HttpContext in order to bypass authorization on management endpoints.
/// </summary>
internal interface ILocalRatatoskrRequestFeature;

#pragma warning disable MA0182 // marker type for in-process request detection, not yet wired to a caller
internal sealed class LocalRatatoskrRequestFeature : ILocalRatatoskrRequestFeature;
#pragma warning restore MA0182
