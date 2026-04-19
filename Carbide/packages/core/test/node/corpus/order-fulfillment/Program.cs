using OrderFulfillment;

var orders = new[]
{
    new Order(
        Id: "A100",
        Region: "US",
        IsPriority: false,
        Lines: new[]
        {
            new OrderLine("keyboard", 1, 70m, false),
            new OrderLine("monitor", 2, 180m, true),
        }),
    new Order(
        Id: "B200",
        Region: "EU",
        IsPriority: true,
        Lines: new[]
        {
            new OrderLine("dock", 3, 45m, false),
            new OrderLine("headset", 1, 80m, false),
        }),
};

foreach (var quote in orders.Select(PricingEngine.Quote))
{
    Console.WriteLine(quote.ToDisplay());
}
