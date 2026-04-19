namespace Commerce;

public sealed record Order(string CustomerId, string Region, IReadOnlyList<OrderLine> Lines);

public sealed record OrderLine(string Product, int Quantity, int UnitPrice)
{
    public int Total => Quantity * UnitPrice;
}
