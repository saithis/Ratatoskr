using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Outbox.OutboxSuccess;

[RatatoskrMessage("outbox-success.order-placed")]
public sealed record OutboxSuccessOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("outbox-success.process-order-command")]
public sealed record OutboxSuccessProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("outbox-success.reserve-stock-internal")]
public sealed record OutboxSuccessReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("outbox-success.order-fulfilled")]
public sealed record OutboxSuccessOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
