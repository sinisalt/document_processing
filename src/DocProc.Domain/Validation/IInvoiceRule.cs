using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation;

/// <summary>One validation rule. Rules are pure: same invoice and context, same flags.</summary>
public interface IInvoiceRule
{
    IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context);
}
