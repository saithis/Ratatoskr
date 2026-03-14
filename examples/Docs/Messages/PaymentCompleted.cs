using Ratatoskr;

namespace Docs.Messages;

#region PaymentCompleted
[RatatoskrMessage("order.payment-completed")]
public record PaymentCompleted(Guid OrderId, string TransactionId);
#endregion
