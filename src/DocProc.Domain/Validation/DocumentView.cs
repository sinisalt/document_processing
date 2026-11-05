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

    private DocumentView(DocumentTypeKey type, string? json, Invoice? invoice)
    {
        Type = type;
        _json = json;
        _invoice = invoice;
        _invoiceRead = invoice is not null;
    }

    public DocumentTypeKey Type { get; }

    /// <summary>
    /// The values view of the canonical tree (the <c>doc</c> block of docs/11 §5.6: plain values, Missing as null),
    /// which the generic rule kinds read; null when the caller has no definition to build it from. Provenance
    /// (status, confidence) stays in <see cref="Root"/>.
    /// </summary>
    public JsonObject? Values { get; private set; }

    /// <summary>The type key as the definition names it (<c>CreditNote</c>, <c>price-list</c>), for flag parameters.</summary>
    public string TypeKey => Type.Value;

    /// <summary>The role bindings of the type (role name to field path, <c>documentDate</c> to <c>issueDate</c>), for rules that read a role.</summary>
    public IReadOnlyDictionary<string, string> RolePaths { get; private set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Attaches the values view built by the caller (<c>DocumentDataView.Values</c>) and the type's role bindings;
    /// returns this view.
    /// </summary>
    public DocumentView WithValues(JsonObject? values, IReadOnlyDictionary<string, string>? rolePaths = null)
    {
        Values = values;
        RolePaths = rolePaths ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return this;
    }

    /// <summary>
    /// The entry of <see cref="RolePaths"/> that names the <c>duplicateKey</c> components for messages: the fields' labels
    /// and "supplier" for <c>$supplierId</c>, joined with commas (the <c>duplicateKey</c> entry holds the paths).
    /// </summary>
    public const string DuplicateKeyLabelsRole = "duplicateKey.labels";

    /// <summary>The path bound to a role, or null.</summary>
    public string? RolePath(string role) => RolePaths.GetValueOrDefault(role);

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
    public static DocumentView FromJson(DocumentTypeKey type, string? canonicalJson) => new(type, canonicalJson, null);

    /// <summary>A view over an invoice already in memory; its tree is serialised on demand.</summary>
    public static DocumentView ForInvoice(Invoice invoice, DocumentTypeKey? type = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new DocumentView(type ?? DocumentTypeKey.Invoice, null, invoice);
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
    public static bool IsInvoiceCompatible(DocumentTypeKey type) => type.IsInvoiceFamily;

    public static Invoice? Of(DocumentView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return IsInvoiceCompatible(view.Type) ? view.ProjectInvoice() : null;
    }
}
