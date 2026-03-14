using Ratatoskr;

namespace Docs.Messages;

#region ShipOrder
[RatatoskrMessage("order.ship")]
public record ShipOrder(Guid OrderId, string ShippingAddress);
#endregion
