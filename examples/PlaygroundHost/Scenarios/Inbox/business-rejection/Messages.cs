using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Inbox.BusinessRejection;

[RatatoskrMessage("business-rejection.order-placed")]
public sealed record BusinessRejectionOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("business-rejection.process-order-command")]
public sealed record BusinessRejectionProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("business-rejection.reserve-stock-internal")]
public sealed record BusinessRejectionReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("business-rejection.order-fulfilled")]
public sealed record BusinessRejectionOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("business-rejection.order-failed")]
public sealed record BusinessRejectionOrderFailed(string OrderId, string ScenarioRunId, string Reason) : IPlaygroundCorrelatedOrderMessage;
