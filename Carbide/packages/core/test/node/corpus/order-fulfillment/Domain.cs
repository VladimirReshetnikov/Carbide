using System;
using System.Collections.Generic;
using System.Linq;

namespace OrderFulfillment;

public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice, bool IsFragile);

public sealed record Order(string Id, string Region, bool IsPriority, IReadOnlyList<OrderLine> Lines);

public static class PricingEngine
{
    public static ShipmentQuote Quote(Order order)
    {
        var subTotal = order.Lines.Sum(static l => l.Quantity * l.UnitPrice);
        var fragileSurcharge = order.Lines.Any(static l => l.IsFragile) ? 7.50m : 0m;
        var regionalBase = order.Region switch
        {
            "US" => 5m,
            "EU" => 8m,
            _ => 11m,
        };

        var prioritySurcharge = order.IsPriority ? 12m : 0m;
        var shipping = regionalBase + fragileSurcharge + prioritySurcharge;

        return new ShipmentQuote(order.Id, subTotal, shipping, subTotal + shipping);
    }
}

public sealed record ShipmentQuote(string OrderId, decimal SubTotal, decimal Shipping, decimal GrandTotal)
{
    public string ToDisplay() => $"{OrderId}:{SubTotal:0.00}:{Shipping:0.00}:{GrandTotal:0.00}";
}
