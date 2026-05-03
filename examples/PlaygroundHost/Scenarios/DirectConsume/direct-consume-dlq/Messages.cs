using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq;

[RatatoskrMessage("direct-consume-dlq.order-placed")]
public sealed record DirectConsumeDlqOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-dlq.process-order-command")]
public sealed record DirectConsumeDlqProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-dlq.reserve-stock-internal")]
public sealed record DirectConsumeDlqReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-dlq.order-fulfilled")]
public sealed record DirectConsumeDlqOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-dlq.order-failed")]
public sealed record DirectConsumeDlqOrderFailed(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
