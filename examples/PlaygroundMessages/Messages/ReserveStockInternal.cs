using Ratatoskr;

namespace PlaygroundMessages.Messages;

[RatatoskrMessage("orders.internal.reserve-stock")]
public class ReserveStockInternal
{
    public required string OrderId { get; init; }
}
