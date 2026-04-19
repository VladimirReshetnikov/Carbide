namespace Commerce;

public static class SeedData
{
    public static IReadOnlyList<Order> CreateOrders() =>
    [
        new("cust-1", "West", [ new("Keyboard", 2, 30), new("Mouse", 1, 15) ]),
        new("cust-2", "East", [ new("Monitor", 1, 120), new("Keyboard", 1, 30) ]),
        new("cust-1", "West", [ new("Keyboard", 4, 30), new("Cable", 3, 5) ])
    ];
}
