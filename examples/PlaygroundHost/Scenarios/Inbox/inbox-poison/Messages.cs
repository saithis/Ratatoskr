using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Inbox.InboxPoison;

[RatatoskrMessage("inbox-poison.order-placed")]
public sealed record InboxPoisonOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("inbox-poison.process-order-command")]
public sealed record InboxPoisonProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("inbox-poison.reserve-stock-internal")]
public sealed record InboxPoisonReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
