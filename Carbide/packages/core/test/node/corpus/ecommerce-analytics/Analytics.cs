namespace Commerce;

public static class Analytics
{
    public static (string Region, int Revenue) TopRegionByRevenue(IEnumerable<Order> orders)
    {
        var top = orders
            .GroupBy(o => o.Region)
            .Select(g => new { Region = g.Key, Revenue = g.Sum(o => o.Lines.Sum(l => l.Total)) })
            .OrderByDescending(x => x.Revenue)
            .ThenBy(x => x.Region, StringComparer.Ordinal)
            .First();

        return (top.Region, top.Revenue);
    }

    public static (string Product, int Quantity) TopProductByQuantity(IEnumerable<Order> orders)
    {
        var top = orders
            .SelectMany(o => o.Lines)
            .GroupBy(l => l.Product)
            .Select(g => new { Product = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .OrderByDescending(x => x.Quantity)
            .ThenBy(x => x.Product, StringComparer.Ordinal)
            .First();

        return (top.Product, top.Quantity);
    }

    public static int CountRepeatCustomers(IEnumerable<Order> orders) =>
        orders
            .GroupBy(o => o.CustomerId)
            .Count(g => g.Count() > 1);
}
