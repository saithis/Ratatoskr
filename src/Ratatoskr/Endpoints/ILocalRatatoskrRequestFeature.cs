namespace Ratatoskr.Endpoints;

/// <summary>
/// Marker feature set only by in-process proxy dispatch. Cannot be spoofed via HTTP.
/// Authorization handlers check for this feature to bypass policy checks for local backends.
/// </summary>
public interface ILocalRatatoskrRequestFeature { }

internal sealed class LocalRatatoskrRequestFeature : ILocalRatatoskrRequestFeature { }
