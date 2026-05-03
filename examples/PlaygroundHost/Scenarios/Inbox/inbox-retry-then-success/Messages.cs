using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess;

[RatatoskrMessage("inbox-retry-then-success.order-placed")]
public sealed record InboxRetryThenSuccessOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("inbox-retry-then-success.process-order-command")]
public sealed record InboxRetryThenSuccessProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("inbox-retry-then-success.reserve-stock-internal")]
public sealed record InboxRetryThenSuccessReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("inbox-retry-then-success.order-fulfilled")]
public sealed record InboxRetryThenSuccessOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("inbox-retry-then-success.order-failed")]
public sealed record InboxRetryThenSuccessOrderFailed(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
