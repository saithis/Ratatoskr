using Ratatoskr;

namespace PlaygroundMessages.Messages;

[RatatoskrMessage("ecommerce.order.placed")]
public class OrderPlaced
{
    public required string OrderId { get; init; }

    /// <summary>Demo-only: used by <c>POST /api/orders/oversized</c> to exceed outbox max message size.</summary>
    public string? BulkPaddingForDemo { get; init; }
}
