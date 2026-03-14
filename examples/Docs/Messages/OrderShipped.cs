using Ratatoskr;

namespace Docs.Messages;

#region OrderShipped
[RatatoskrMessage("order.shipped")]
public record OrderShipped(Guid OrderId, string TrackingNumber);
#endregion
