using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced;

[RatatoskrMessage("fanout-two-handlers-on-orderplaced.order-placed")]
public sealed record FanoutTwoHandlersOnOrderplacedOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("fanout-two-handlers-on-orderplaced.process-order-command")]
public sealed record FanoutTwoHandlersOnOrderplacedProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("fanout-two-handlers-on-orderplaced.reserve-stock-internal")]
public sealed record FanoutTwoHandlersOnOrderplacedReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("fanout-two-handlers-on-orderplaced.order-fulfilled")]
public sealed record FanoutTwoHandlersOnOrderplacedOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("fanout-two-handlers-on-orderplaced.order-failed")]
public sealed record FanoutTwoHandlersOnOrderplacedOrderFailed(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
