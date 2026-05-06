using Ratatoskr;

namespace OrderService.Messages;

[RatatoskrMessage("com.example.orders.created")]
public class OrderCreatedEvent
{
    public required Guid OrderId { get; init; }
    public required string CustomerName { get; init; }
    public required decimal TotalAmount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
