using Ratatoskr;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;

[RatatoskrMessage("ecommerce.order.failed")]
public class OrderFailed
{
    public required string OrderId { get; init; }
    public required string Reason { get; init; }
    public string ScenarioRunId { get; init; } = "";
}
