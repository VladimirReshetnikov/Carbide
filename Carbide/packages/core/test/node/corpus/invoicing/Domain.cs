namespace Corp.Billing;

public sealed record InvoiceLine(string Sku, int Quantity, decimal UnitPrice, bool IsTaxable)
{
    public decimal Subtotal => Quantity * UnitPrice;
}

public sealed record Invoice(string Customer, string CountryCode, decimal DiscountRate, IReadOnlyList<InvoiceLine> Lines);
