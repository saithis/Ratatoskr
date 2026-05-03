using Ratatoskr;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;

[RatatoskrMessage("ecommerce.order.placed")]
public class OrderPlaced
{
    public required string OrderId { get; init; }

    /// <summary>Correlation for dashboard and scenario runner.</summary>
    public string ScenarioRunId { get; init; } = "";

    /// <summary>Demo-only: used by oversized outbox scenario to exceed max message size.</summary>
    public string? BulkPaddingForDemo { get; init; }
}
