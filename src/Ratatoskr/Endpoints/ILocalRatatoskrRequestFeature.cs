namespace Ratatoskr.Endpoints;

/// <summary>
/// Marker feature set only by in-process proxy dispatch. Cannot be spoofed via HTTP,
/// and internal so that third-party code cannot attach a custom implementation to an
/// HttpContext in order to bypass authorization on management endpoints.
/// </summary>
internal interface ILocalRatatoskrRequestFeature { }

internal sealed class LocalRatatoskrRequestFeature : ILocalRatatoskrRequestFeature { }
