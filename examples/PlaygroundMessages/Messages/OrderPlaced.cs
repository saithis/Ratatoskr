using Ratatoskr;

namespace PlaygroundMessages.Messages;

[RatatoskrMessage("ecommerce.order.placed")]
public class OrderPlaced
{
    public required string OrderId { get; init; }
}
