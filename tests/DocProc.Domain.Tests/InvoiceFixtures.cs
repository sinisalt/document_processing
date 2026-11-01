using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation;

namespace DocProc.Domain.Tests;

/// <summary>A small, arithmetically consistent German-style invoice used as the baseline for rule tests.</summary>
internal static class InvoiceFixtures
{
    public static readonly DateOnly Today = new(2026, 10, 30);

    public static readonly TenantContext Tenant = new("Gasthaus zur Post GmbH", "DE123456789", "EUR", ["Gasthaus zur Post"]);

    public static ValidationContext Context => ValidationContext.For(Today, Tenant);

    private static readonly FieldSource Llm = new(SourceMethod.Llm, Page: 1);

    public static Money Eur(decimal amount) => new(amount, "EUR");

    public static Invoice Valid() => new()
    {
        Kind = FieldValue.Extracted(InvoiceKind.Invoice, Llm, 0.99),
        InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("RE-2026-0042"), Llm, 0.98),
        IssueDate = FieldValue.Extracted(new DateOnly(2026, 10, 27), Llm, 0.97),
        DueDate = FieldValue.Extracted(new DateOnly(2026, 11, 10), Llm, 0.95),
        Currency = FieldValue.Extracted("EUR", Llm, 0.99),
        Supplier = FieldValue.Extracted(new Party("METRO Deutschland GmbH", VatId: "DE811177936"), Llm, 0.96),
        Customer = FieldValue.Extracted(new Party("Gasthaus zur Post GmbH", VatId: "DE 123 456 789"), Llm, 0.96),
        SubtotalNet = FieldValue.Extracted(Eur(150.00m), Llm, 0.99),
        TaxBreakdown =
        [
            new TaxBreakdownLine(7m, Eur(100.00m), Eur(7.00m), TaxCategory.Standard),
            new TaxBreakdownLine(19m, Eur(50.00m), Eur(9.50m), TaxCategory.Standard),
        ],
        TotalTax = FieldValue.Extracted(Eur(16.50m), Llm, 0.99),
        TotalGross = FieldValue.Extracted(Eur(166.50m), Llm, 0.99),
        Lines =
        [
            new InvoiceLine
            {
                LineNumber = 1,
                Description = FieldValue.Extracted("Tomaten Strauch 5 kg", Llm, 0.95),
                Quantity = FieldValue.Extracted(10m, Llm, 0.95),
                Unit = FieldValue.Extracted(UnitOfMeasure.Kilogram, Llm, 0.95),
                UnitRaw = "kg",
                UnitPrice = FieldValue.Extracted(Eur(10.00m), Llm, 0.95),
                LineNet = FieldValue.Extracted(Eur(100.00m), Llm, 0.95),
                TaxRate = FieldValue.Extracted(7m, Llm, 0.95),
            },
            new InvoiceLine
            {
                LineNumber = 2,
                Description = FieldValue.Extracted("Mineralwasser 12 x 0,75 l", Llm, 0.95),
                Quantity = FieldValue.Extracted(5m, Llm, 0.95),
                Unit = FieldValue.Extracted(UnitOfMeasure.Crate, Llm, 0.95),
                UnitRaw = "Kiste",
                UnitPrice = FieldValue.Extracted(Eur(10.00m), Llm, 0.95),
                LineNet = FieldValue.Extracted(Eur(50.00m), Llm, 0.95),
                TaxRate = FieldValue.Extracted(19m, Llm, 0.95),
            },
        ],
    };
}
