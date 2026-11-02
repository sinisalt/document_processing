using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Documents;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation;

/// <summary>
/// What a <see cref="IDocumentRule"/> reads (docs/11 §11): the document's type and its canonical tree. Rules of the
/// generic kinds read the tree; the coded invoice kinds read the typed <see cref="InvoiceProjection"/> of it. Both are
/// built on first use and cached, so a rule set that runs eight coded rules deserialises the invoice once. Rules must
/// not change what they read.
/// </summary>
public sealed class DocumentView
{
    private readonly string? _json;
    private JsonObject? _root;
    private bool _rootRead;
    private Invoice? _invoice;
    private bool _invoiceRead;

    private DocumentView(DocumentType type, string? json, Invoice? invoice)
    {
        Type = type;
        _json = json;
        _invoice = invoice;
        _invoiceRead = invoice is not null;
    }

    public DocumentType Type { get; }

    /// <summary>The canonical tree, or null when the document has none. Parsed on first use.</summary>
    public JsonObject? Root
    {
        get
        {
            if (!_rootRead)
            {
                _root = _json is not null
                    ? JsonNode.Parse(_json) as JsonObject
                    : _invoice is not null ? JsonSerializer.SerializeToNode(_invoice, CanonicalJson.Options) as JsonObject : null;
                _rootRead = true;
            }

            return _root;
        }
    }

    /// <summary>A view over the stored canonical JSON, as the validate stage reads it.</summary>
    public static DocumentView FromJson(DocumentType type, string? canonicalJson) => new(type, canonicalJson, null);

    /// <summary>A view over an invoice already in memory; its tree is serialised on demand.</summary>
    public static DocumentView ForInvoice(Invoice invoice, DocumentType type = DocumentType.Invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new DocumentView(type, null, invoice);
    }

    internal Invoice? ProjectInvoice()
    {
        if (!_invoiceRead)
        {
            // The same call the validate stage made before the rule seam: a malformed tree fails the same way.
            _invoice = _json is null
                ? null
                : JsonSerializer.Deserialize<Invoice>(_json, CanonicalJson.Options) ?? throw new JsonException("Canonical invoice JSON was null.");
            _invoiceRead = true;
        }

        return _invoice;
    }
}

/// <summary>
/// The typed invoice reading of a <see cref="DocumentView"/>, which the coded invoice rule kinds run on
/// (<see cref="InvoiceRuleAdapter"/>). Only invoice-compatible types have one; for every other type, and for a
/// document without canonical data, it is null.
/// </summary>
public static class InvoiceProjection
{
    /// <summary>Whether documents of this type hold an invoice tree. Until types are data (P1) these are the two coded invoice types.</summary>
    public static bool IsInvoiceCompatible(DocumentType type) => type is DocumentType.Invoice or DocumentType.CreditNote;

    public static Invoice? Of(DocumentView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return IsInvoiceCompatible(view.Type) ? view.ProjectInvoice() : null;
    }
}
