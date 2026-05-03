using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq;

[RatatoskrMessage("direct-consume-dlq.order-placed")]
public sealed record DirectConsumeDlqOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
