using Commerce;

var orders = SeedData.CreateOrders();

var topRegion = Analytics.TopRegionByRevenue(orders);
var topProduct = Analytics.TopProductByQuantity(orders);
var repeatCustomers = Analytics.CountRepeatCustomers(orders);

Console.WriteLine($"TopRegion={topRegion.Region};Revenue={topRegion.Revenue}");
Console.WriteLine($"TopProduct={topProduct.Product};Qty={topProduct.Quantity}");
Console.WriteLine($"RepeatCustomers={repeatCustomers}");
