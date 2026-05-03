using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry;

[RatatoskrMessage("direct-consume-retry.order-placed")]
public sealed record DirectConsumeRetryOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-retry.process-order-command")]
public sealed record DirectConsumeRetryProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-retry.reserve-stock-internal")]
public sealed record DirectConsumeRetryReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("direct-consume-retry.order-fulfilled")]
public sealed record DirectConsumeRetryOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
