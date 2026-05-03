using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Outbox.OutboxPoison;

[RatatoskrMessage("outbox-poison.order-placed")]
public sealed record OutboxPoisonOrderPlaced(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("outbox-poison.process-order-command")]
public sealed record OutboxPoisonProcessOrderCommand(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("outbox-poison.reserve-stock-internal")]
public sealed record OutboxPoisonReserveStockInternal(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;

[RatatoskrMessage("outbox-poison.order-fulfilled")]
public sealed record OutboxPoisonOrderFulfilled(string OrderId, string ScenarioRunId) : IPlaygroundCorrelatedOrderMessage;
