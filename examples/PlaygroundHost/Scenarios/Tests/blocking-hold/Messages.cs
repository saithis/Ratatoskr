using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Tests.BlockingHold;

[RatatoskrMessage("blocking-hold.order-placed")]
public sealed record BlockingHoldOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("blocking-hold.process-order-command")]
public sealed record BlockingHoldProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("blocking-hold.reserve-stock-internal")]
public sealed record BlockingHoldReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("blocking-hold.order-fulfilled")]
public sealed record BlockingHoldOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("blocking-hold.order-failed")]
public sealed record BlockingHoldOrderFailed(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
