using Ratatoskr;

namespace PlaygroundMessages.Messages;

[RatatoskrMessage("ecommerce.inventory.process")]
public class ProcessOrderCommand
{
    public required string OrderId { get; init; }
}
