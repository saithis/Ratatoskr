using Ratatoskr;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;

[RatatoskrMessage("ecommerce.order.fulfilled")]
public class OrderFulfilled
{
    public required string OrderId { get; init; }
    public string ScenarioRunId { get; init; } = "";
}
