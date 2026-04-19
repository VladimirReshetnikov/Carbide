namespace Corp.Billing;

public static class InvoiceEngine
{
    private static readonly IReadOnlyDictionary<string, decimal> TaxRates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["US-WA"] = 0.102m,
        ["US-OR"] = 0.000m,
        ["DE"] = 0.190m,
    };

    public static string CalculateSummary(Invoice invoice)
    {
        var subtotal = invoice.Lines.Sum(static line => line.Subtotal);
        var discount = Math.Round(subtotal * invoice.DiscountRate, 2, MidpointRounding.AwayFromZero);
        var discounted = subtotal - discount;

        var taxableBase = invoice.Lines
            .Where(static line => line.IsTaxable)
            .Sum(static line => line.Subtotal);
        var normalizedTaxableBase = subtotal == 0m ? 0m : taxableBase / subtotal * discounted;

        var taxRate = TaxRates.TryGetValue(invoice.CountryCode, out var known) ? known : 0.15m;
        var tax = Math.Round(normalizedTaxableBase * taxRate, 2, MidpointRounding.AwayFromZero);
        var total = discounted + tax;

        return $"customer={invoice.Customer};subtotal={subtotal:0.00};discount={discount:0.00};tax={tax:0.00};total={total:0.00}";
    }
}
