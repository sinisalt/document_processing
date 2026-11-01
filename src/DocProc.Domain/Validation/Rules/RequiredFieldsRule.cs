using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation.Rules;

/// <summary>MISSING_REQUIRED for every required header field that is missing or unreadable (docs/02-data-model.md §4.2).</summary>
public sealed class RequiredFieldsRule : IInvoiceRule
{
    public IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context)
    {
        foreach (var (path, field) in Required(invoice))
        {
            if (!field.HasValue)
            {
                var unreadable = field.Status == FieldStatus.Unreadable;
                var why = unreadable ? "could not be read" : "was not found";
                yield return FlagCodes.Create(
                    FlagCodes.MissingRequired,
                    $"Required field '{path}' {why}.",
                    path,
                    FlagParams.Of((FlagParams.Reason, unreadable ? "unreadable" : "notFound"), ("field", path)));
            }
        }

        if (invoice.Supplier.HasValue && string.IsNullOrWhiteSpace(invoice.Supplier.Value!.Name))
        {
            yield return FlagCodes.Create(FlagCodes.MissingRequired, "Supplier name was not found.", "supplier.name", FlagParams.Of((FlagParams.Reason, "supplierName")));
        }

        var taxable = invoice.TotalTax.HasValue && invoice.TotalTax.Value.Amount != 0;
        if (taxable && invoice.TaxBreakdown.Count == 0)
        {
            yield return FlagCodes.Create(FlagCodes.MissingRequired, "The document carries tax but no tax breakdown was found.", "taxBreakdown", FlagParams.Of((FlagParams.Reason, "taxBreakdown")));
        }
    }

    private static IEnumerable<(string Path, IFieldValue Field)> Required(Invoice invoice)
    {
        yield return ("kind", invoice.Kind);
        yield return ("invoiceNumber", invoice.InvoiceNumber);
        yield return ("issueDate", invoice.IssueDate);
        yield return ("currency", invoice.Currency);
        yield return ("supplier", invoice.Supplier);
        yield return ("customer", invoice.Customer);
        yield return ("subtotalNet", invoice.SubtotalNet);
        yield return ("totalTax", invoice.TotalTax);
        yield return ("totalGross", invoice.TotalGross);

        if (invoice.Kind is { HasValue: true, Value: InvoiceKind.CreditNote })
        {
            yield return ("originalInvoiceReference", invoice.OriginalInvoiceReference);
        }
    }
}
