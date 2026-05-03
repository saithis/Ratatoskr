using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Other.EfcoreInternalCommand;

[RatatoskrMessage("efcore-internal-command.order-placed")]
public sealed record EfcoreInternalCommandOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("efcore-internal-command.process-order-command")]
public sealed record EfcoreInternalCommandProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("efcore-internal-command.reserve-stock-internal")]
public sealed record EfcoreInternalCommandReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("efcore-internal-command.order-fulfilled")]
public sealed record EfcoreInternalCommandOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
