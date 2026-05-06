namespace PlaygroundHost.Persistence.Entities;

public class Order
{
    public Guid Id { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime StatusChangedAt { get; set; }

    /// <summary>How the initial order messages left this service: <c>outbox</c> or <c>direct</c>.</summary>
    public string PublishOrigin { get; set; } = "outbox";
}

public enum OrderStatus
{
    Placed,
    Fulfilled,
    Failed,
}
