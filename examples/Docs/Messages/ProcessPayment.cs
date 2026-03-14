using Ratatoskr;

namespace Docs.Messages;

#region ProcessPayment
[RatatoskrMessage("order.process-payment")]
public record ProcessPayment(Guid OrderId, decimal Amount);
#endregion
