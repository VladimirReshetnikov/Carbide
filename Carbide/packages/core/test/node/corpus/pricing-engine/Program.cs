using Pricing;

var items = new[]
{
    new CartItem("book", 2, 25),
    new CartItem("pen", 10, 2),
    new CartItem("notebook", 3, 8)
};

var engine = new PricingEngine(
[
    new BulkDiscountRule(minimumQuantity: 5, percentOff: 10),
    new CategoryDiscountRule(category: "book", percentOff: 5),
]);

var quote = engine.Quote(items);

Console.WriteLine($"Subtotal={quote.Subtotal}");
Console.WriteLine($"Discount={quote.Discount}");
Console.WriteLine($"Total={quote.Total}");
