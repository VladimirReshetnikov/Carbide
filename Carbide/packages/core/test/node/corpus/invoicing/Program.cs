using Corp.Billing;

var invoice = new Invoice(
    Customer: "Northwind",
    CountryCode: "US-WA",
    DiscountRate: 0.075m,
    Lines:
    [
        new InvoiceLine("LAPTOP", 2, 899.99m, IsTaxable: true),
        new InvoiceLine("SUPPORT", 1, 120.00m, IsTaxable: false),
        new InvoiceLine("DOCK", 3, 149.50m, IsTaxable: true),
    ]);

Console.Write(InvoiceEngine.CalculateSummary(invoice));
