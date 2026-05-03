using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess;

[RatatoskrMessage("outbox-retry-then-success.order-placed")]
public sealed record OutboxRetryThenSuccessOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("outbox-retry-then-success.process-order-command")]
public sealed record OutboxRetryThenSuccessProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("outbox-retry-then-success.reserve-stock-internal")]
public sealed record OutboxRetryThenSuccessReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("outbox-retry-then-success.order-fulfilled")]
public sealed record OutboxRetryThenSuccessOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
