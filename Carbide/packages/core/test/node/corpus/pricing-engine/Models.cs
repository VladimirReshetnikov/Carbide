namespace Pricing;

public sealed record CartItem(string Category, int Quantity, int UnitPrice)
{
    public int Subtotal => Quantity * UnitPrice;
}

public sealed record Quote(int Subtotal, int Discount)
{
    public int Total => Subtotal - Discount;
}
