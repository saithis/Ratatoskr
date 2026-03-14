using Ratatoskr;

namespace Docs.Messages;

#region SendOrderConfirmation
[RatatoskrMessage("order.send-confirmation")]
public record SendOrderConfirmation(Guid OrderId, string CustomerEmail);
#endregion
