namespace DocProc.Extraction.EInvoice;

/// <summary>
/// Raised when an e-invoice that was detected as UBL or CII cannot be parsed: malformed XML, a prohibited DTD,
/// or a root element that does not match the detection. Invalid values inside a well-formed document never throw;
/// they become <see cref="Domain.Fields.FieldStatus.Unreadable"/> fields (docs/02-data-model.md §3).
/// </summary>
public sealed class EInvoiceParseException : Exception
{
    public EInvoiceParseException()
    {
    }

    public EInvoiceParseException(string message)
        : base(message)
    {
    }

    public EInvoiceParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
