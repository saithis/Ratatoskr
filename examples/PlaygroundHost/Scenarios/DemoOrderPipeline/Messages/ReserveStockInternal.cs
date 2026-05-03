using Ratatoskr;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;

[RatatoskrMessage("orders.internal.reserve-stock")]
public class ReserveStockInternal
{
    public required string OrderId { get; init; }
    public string ScenarioRunId { get; init; } = "";
}
