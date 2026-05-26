namespace PlaygroundHost;

/// <summary>
/// Assembly entry marker for MVC testing WebApplicationFactory so tests can target this app
/// without colliding with other Program types in the same test assembly.
/// </summary>
public sealed class PlaygroundHostAppMarker
{
    private PlaygroundHostAppMarker() { }

    /// <summary>Dummy method to prevent MA0036 analyzer warning.</summary>
    public void Dummy() { }
}
