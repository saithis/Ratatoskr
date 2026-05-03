using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Other.ReplayDedups;

[RatatoskrMessage("replay-dedups.order-placed")]
public sealed record ReplayDedupsOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("replay-dedups.process-order-command")]
public sealed record ReplayDedupsProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("replay-dedups.reserve-stock-internal")]
public sealed record ReplayDedupsReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("replay-dedups.order-fulfilled")]
public sealed record ReplayDedupsOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("replay-dedups.order-failed")]
public sealed record ReplayDedupsOrderFailed(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
