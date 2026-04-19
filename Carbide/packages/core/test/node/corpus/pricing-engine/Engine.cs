namespace Pricing;

public sealed class PricingEngine(IReadOnlyList<IDiscountRule> rules)
{
    public Quote Quote(IReadOnlyList<CartItem> items)
    {
        var subtotal = items.Sum(i => i.Subtotal);
        var discount = rules.Sum(r => r.Compute(items));
        return new Quote(subtotal, discount);
    }
}
