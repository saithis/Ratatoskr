using PlaygroundHost.Infrastructure;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Outbox.OversizedPayloadRollsBack;

[RatatoskrMessage("oversized-payload-rolls-back.order-placed")]
public sealed record OversizedPayloadRollsBackOrderPlaced(
    string OrderId,
    string ScenarioRunId,
    string? BulkPaddingForDemo) : IPlaygroundCorrelatedOrderMessage;
