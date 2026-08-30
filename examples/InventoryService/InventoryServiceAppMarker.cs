namespace InventoryService;

/// <summary>
/// Assembly entry marker for MVC testing WebApplicationFactory so tests can target this app
/// without colliding with other Program types in the same test assembly.
/// </summary>
public sealed class InventoryServiceAppMarker
{
    private InventoryServiceAppMarker() { }

    /// <summary>Dummy method to prevent MA0036 analyzer warning.</summary>
    public void Dummy() { }
}
