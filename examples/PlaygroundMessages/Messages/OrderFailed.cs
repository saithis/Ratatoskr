using Ratatoskr;

namespace PlaygroundMessages.Messages;

[RatatoskrMessage("ecommerce.order.failed")]
public class OrderFailed
{
    public required string OrderId { get; init; }
    public required string Reason { get; init; }
}
