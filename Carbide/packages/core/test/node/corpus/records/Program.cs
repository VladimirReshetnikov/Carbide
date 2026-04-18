using Shop;

var product = new Product("Carbide bit", 12.50m);
var order = new Order(product, 3);
Console.WriteLine($"{order.Item.Name} x{order.Quantity} = {order.Total:F2}");
