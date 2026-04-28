namespace OrderService.Database.Entities;

public class Order
{
    public Guid Id { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum OrderStatus
{
    Placed,
    Fulfilled,
    Failed,
}
