using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Tests.CancelSmoke;

[RatatoskrMessage("cancel-smoke.order-placed")]
public sealed record CancelSmokeOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("cancel-smoke.process-order-command")]
public sealed record CancelSmokeProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("cancel-smoke.reserve-stock-internal")]
public sealed record CancelSmokeReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("cancel-smoke.order-fulfilled")]
public sealed record CancelSmokeOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("cancel-smoke.order-failed")]
public sealed record CancelSmokeOrderFailed(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
