using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess;

[RatatoskrMessage("direct-consume-success.order-placed")]
public sealed record DirectConsumeSuccessOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-success.process-order-command")]
public sealed record DirectConsumeSuccessProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-success.reserve-stock-internal")]
public sealed record DirectConsumeSuccessReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-success.order-fulfilled")]
public sealed record DirectConsumeSuccessOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-success.order-failed")]
public sealed record DirectConsumeSuccessOrderFailed(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
