using Ratatoskr;

namespace Docs.Messages;

#region OrderPlaced
[RatatoskrMessage("order.placed")]
public record OrderPlaced(Guid OrderId, string CustomerEmail, decimal Total);
#endregion
