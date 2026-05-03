namespace InventoryService;

public enum InventoryDemoMode
{
    /// <summary>Fulfill order (happy path).</summary>
    Off,

    /// <summary>Throw until inbox poison.</summary>
    Throw,

    /// <summary>Throw N times then fulfill (inbox retry then success).</summary>
    SucceedAfter,

    /// <summary>Stage <see cref="PlaygroundMessages.Messages.OrderFailed"/>.</summary>
    Reject,
}
