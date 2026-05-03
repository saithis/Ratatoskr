using Ratatoskr;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;

[RatatoskrMessage("ecommerce.inventory.process")]
public class ProcessOrderCommand
{
    public required string OrderId { get; init; }
    public string ScenarioRunId { get; init; } = "";
}
