using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Text;
using DocProc.Llm.Gateway;

namespace DocProc.Llm.Tests.Extraction;

/// <summary>Gateway stand-in: answers in order from a script and keeps every request for assertions.</summary>
internal sealed class FakeGateway : ILlmGateway
{
    private readonly Queue<Func<LlmRequest, LlmResult>> _answers = new();

    public List<LlmRequest> Requests { get; } = [];

    public FakeGateway Answer(
        string json,
        string stopReason = StopReasons.EndTurn,
        LlmUsage? usage = null,
        string model = "claude-opus-5-5",
        LlmProviderKind? provider = null,
        KeyOwner? keyOwner = null)
    {
        _answers.Enqueue(_ => new LlmResult
        {
            Json = json,
            Usage = usage ?? new LlmUsage(1_000, 200),
            Model = model,
            StopReason = stopReason,
            Provider = provider,
            KeyOwner = keyOwner,
        });
        return this;
    }

    public FakeGateway Fail(LlmException exception)
    {
        _answers.Enqueue(_ => throw exception);
        return this;
    }

    public Task<LlmResult> CompleteAsync(Guid tenantId, LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (!_answers.TryDequeue(out var answer))
        {
            throw new InvalidOperationException("The fake gateway has no scripted answer left.");
        }

        return Task.FromResult(answer(request));
    }
}

internal static class SampleAnswers
{
    /// <summary>The schema-shaped answer from SchemaPortabilityTests, reused so the mapping test covers a known-good document.</summary>
    public const string Invoice = """
        {
          "kind": "Invoice", "invoiceNumber": "RE-2026-0042", "issueDate": "2026-10-27", "deliveryDate": null,
          "servicePeriodFrom": null, "servicePeriodTo": null, "dueDate": "2026-11-10", "currency": "EUR",
          "supplier": { "name": "Musterhandel GmbH", "vatId": "DE000000000", "taxId": null, "registrationNumber": null,
                        "addressLine1": null, "addressLine2": null, "postalCode": null, "city": null, "countryCode": "DE",
                        "email": null, "phone": null, "iban": null, "bic": null },
          "customer": null, "orderReferences": ["PO-77"], "deliveryNoteReferences": [], "originalInvoiceReference": null,
          "subtotalNet": 150.00,
          "taxBreakdown": [ { "ratePercent": 7, "taxableAmount": 100, "taxAmount": 7, "categoryCode": "S" } ],
          "totalTax": 16.50, "totalGross": 166.50, "amountDue": null, "rounding": null,
          "chargesAndAllowances": [], "paymentTerms": null, "notes": null,
          "lines": [ { "lineNumber": 1, "supplierSku": "123", "gtin": null, "description": "Tomaten", "quantity": 10, "unit": "kg",
                       "packSize": null, "unitPrice": 10, "discountPercent": null, "discountAmount": null, "lineNet": 100,
                       "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 } ],
          "unreadableRegions": [ { "page": 2, "area": "stamp over totals", "reason": "ink smudge" } ]
        }
        """;

    public const string Classification = """{ "documentType": "CreditNote", "confidence": 0.82, "language": "de", "supplierNameHint": "Musterhandel GmbH" }""";

    public const string Split = """{ "documents": [ { "startPage": 1, "endPage": 2, "documentType": "Invoice", "reason": null }, { "startPage": 3, "endPage": 4, "documentType": "DeliveryNote", "reason": "new header on page 3" } ] }""";
}

/// <summary>Builds positioned text layers in the 1000 × 1000 page space, word by word with explicit boxes.</summary>
internal sealed class TextLayerBuilder
{
    private readonly List<TextPage> _pages = [];
    private List<PositionedWord> _words = [];
    private int _page;
    private double _x;
    private double _y;

    public TextLayerBuilder Page(int number)
    {
        Flush();
        _page = number;
        _x = 50;
        _y = 40;
        return this;
    }

    /// <summary>Words on one line, laid out left to right at 10 units per character.</summary>
    public TextLayerBuilder Line(params string[] words)
    {
        _y += 30;
        _x = 50;
        foreach (var word in words)
        {
            Word(word, _x, _y, _x + (word.Length * 10), _y + 15);
            _x += (word.Length * 10) + 10;
        }

        return this;
    }

    public TextLayerBuilder Word(string text, double x0, double y0, double x1, double y1)
    {
        _words.Add(new PositionedWord(text, new BoundingBox(x0, y0, x1, y1)));
        return this;
    }

    public TextLayer Build(TextLayerSource source = TextLayerSource.PdfText)
    {
        Flush();
        return new TextLayer(source, _pages.ToList());
    }

    private void Flush()
    {
        if (_page > 0)
        {
            _pages.Add(new TextPage(_page, TextPage.NormalizedSize, TextPage.NormalizedSize, _words));
        }

        _words = [];
    }
}

internal static class Inputs
{
    public static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid DocumentId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    public static readonly TenantHints Tenant = new("Gasthaus zur Post GmbH", "DE123456789", "EUR");
    public static readonly byte[] PdfBytes = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];

    /// <summary>A 1×1 PNG header (signature + IHDR) is enough for dimension parsing; the rest is irrelevant to the tests.</summary>
    public static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x7B, 0x00, 0x00, 0x01, 0x2C, 0x08, 0x06, 0x00, 0x00, 0x00,
    ];

    /// <summary>The text layer the sample answer was "read" from: one value per locatability level.</summary>
    public static TextLayer SampleTextLayer() => new TextLayerBuilder()
        .Page(1)
        .Word("Musterhandel", 50, 40, 170, 55).Word("GmbH", 175, 40, 230, 55)
        .Line("USt-IdNr.", "DE", "000000000")
        .Word("Rechnung", 50, 100, 140, 115).Word("Nr.", 145, 100, 175, 115).Word("RE-2026-0042", 180, 100, 300, 115)
        .Line("Datum:", "27.10.2026")
        .Line("Bestellung", "PO-77")
        .Line("1", "123", "Strauchtomaten", "10,00", "kg", "10,00", "100,00", "7", "%")
        .Line("Netto", "150,00")
        .Line("MwSt", "7", "%", "auf", "100,00", "7,00")
        .Line("MwSt", "gesamt", "16,50")
        .Word("Gesamt", 700, 900, 760, 915).Word("166,50", 770, 900, 830, 915).Word("EUR", 835, 900, 870, 915)
        .Page(2)
        .Line("Seite", "2")
        .Build();

    public static ExtractionInput Extraction(
        DocumentType type = DocumentType.Invoice,
        bool pdf = true,
        IReadOnlyList<ReadOnlyMemory<byte>>? images = null,
        TextLayer? textLayer = null,
        SupplierHints? supplier = null,
        TenantHints? tenant = null) =>
        new(TenantId, DocumentId, type, pdf ? PdfBytes : null, images ?? [], textLayer ?? SampleTextLayer(), null, supplier, tenant ?? Tenant);
}
