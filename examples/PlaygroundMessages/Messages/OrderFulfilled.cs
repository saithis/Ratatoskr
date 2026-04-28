using Ratatoskr;

namespace PlaygroundMessages.Messages;

[RatatoskrMessage("ecommerce.order.fulfilled")]
public class OrderFulfilled
{
    public required string OrderId { get; init; }
}
