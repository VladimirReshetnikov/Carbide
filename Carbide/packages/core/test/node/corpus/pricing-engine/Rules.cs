namespace Pricing;

public interface IDiscountRule
{
    int Compute(IReadOnlyList<CartItem> items);
}

public sealed class BulkDiscountRule(int minimumQuantity, int percentOff) : IDiscountRule
{
    public int Compute(IReadOnlyList<CartItem> items)
    {
        var eligible = items.Where(i => i.Quantity >= minimumQuantity).Sum(i => i.Subtotal);
        return eligible * percentOff / 100;
    }
}

public sealed class CategoryDiscountRule(string category, int percentOff) : IDiscountRule
{
    public int Compute(IReadOnlyList<CartItem> items)
    {
        var eligible = items
            .Where(i => string.Equals(i.Category, category, StringComparison.Ordinal))
            .Sum(i => i.Subtotal);
        return eligible * percentOff / 100;
    }
}
