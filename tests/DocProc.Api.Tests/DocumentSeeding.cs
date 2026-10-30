using System.Collections.Concurrent;
using DocProc.Application.Pipeline;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Api.Tests;

/// <summary>What <see cref="ApiFactory.SeedDocumentAsync"/> inserts; the defaults are a reviewable LLM-extracted invoice of tenant A.</summary>
public sealed record DocumentSeed
{
    public Guid TenantId { get; init; } = ApiFactory.TenantA;
    public DocumentStatus Status { get; init; } = DocumentStatus.NeedsReview;
    public DocumentType Type { get; init; } = DocumentType.Invoice;
    public Invoice? Invoice { get; init; } = TestInvoices.Valid();
    public string? SupplierName { get; init; } = "METRO Deutschland GmbH";
    public IReadOnlyList<DocumentFlag> Flags { get; init; } = [];
    public bool ReviewTask { get; init; }
    public string? AssignedTo { get; init; }
    public byte[]? PagePng { get; init; }
    public string? TextLayerJson { get; init; }
    public byte[]? OriginalBytes { get; init; }
    public string FileName { get; init; } = "invoice.pdf";
    public string ContentType { get; init; } = "application/pdf";
    public string? SourceFrom { get; init; } = "scanner@example.test";
    public DateTimeOffset? CreatedAt { get; init; }
}

public sealed record SeededDocument(Guid DocumentId, Guid IngestionId, Guid ItemId, Guid? ReviewTaskId, string? CanonicalJson, byte[] OriginalBytes);

/// <summary>Records what the API enqueued instead of running anything; the pipeline is another block's job.</summary>
public sealed class CapturingPipelineQueue : IPipelineQueue
{
    private readonly ConcurrentQueue<object> _messages = new();

    public IReadOnlyList<ProcessIngestion> Ingestions => _messages.OfType<ProcessIngestion>().ToList();

    public IReadOnlyList<ProcessDocument> Documents => _messages.OfType<ProcessDocument>().ToList();

    public Task EnqueueAsync(ProcessIngestion message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(ProcessDocument message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(ProcessDocument message, TimeSpan delay, CancellationToken cancellationToken) => EnqueueAsync(message, cancellationToken);
}

/// <summary>A small, arithmetically consistent invoice with LLM provenance on every field (mirrors the domain test fixture).</summary>
public static class TestInvoices
{
    private static readonly FieldSource Llm = new(SourceMethod.Llm, Page: 1, BBox: new BoundingBox(100, 50, 300, 70), RawText: "RE-2026-0042");

    /// <summary>Eight bytes of PNG signature plus a payload that differs per call, so each seeded page has its own hash.</summary>
    public static byte[] FakePng() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Guid.NewGuid().ToByteArray()];

    public static Money Eur(decimal amount) => new(amount, "EUR");

    public static Invoice Valid(string number = "RE-2026-0042", DateOnly? issueDate = null) => new()
    {
        Kind = FieldValue.Extracted(InvoiceKind.Invoice, Llm, 0.99),
        InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create(number), Llm, 0.98),
        IssueDate = FieldValue.Extracted(issueDate ?? new DateOnly(2026, 10, 27), Llm, 0.97),
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
                Quantity = FieldValue.Extracted(5m, Llm, 0.72),
                Unit = FieldValue.Extracted(UnitOfMeasure.Crate, Llm, 0.95),
                UnitRaw = "Kiste",
                UnitPrice = FieldValue.Extracted(Eur(10.00m), Llm, 0.95),
                LineNet = FieldValue.Extracted(Eur(50.00m), Llm, 0.95),
                TaxRate = FieldValue.Extracted(19m, Llm, 0.95),
            },
        ],
    };
}
