namespace Shop;

public record Product(string Name, decimal Price);
public record Order(Product Item, int Quantity)
{
    public decimal Total => Item.Price * Quantity;
}
