using System.Text.Json.Nodes;
using DocProc.Application.Documents;
using DocProc.Domain.Common;
using DocProc.Domain.DeliveryNotes;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.PurchaseOrders;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Integration.Tests;

/// <summary>
/// The single <see cref="RoleProjector"/> (docs/11 §6.2) that replaced the Extract stage's and the corrections
/// endpoint's copies of the summary-column mapping. The parity cases compare it with the typed mapping the corrections
/// endpoint used, reproduced here as the oracle.
/// </summary>
public sealed class RoleProjectorTests
{
    public static TheoryData<string> Invoices => new()
    {
        "complete",
        "missing-number",
        "unreadable-total",
        "lower-case-currency",
        "malformed-currency",
        "long-number",
        "conflict",
        "corrected",
        "empty",
    };

    private static Invoice InvoiceCase(string name)
    {
        var complete = new Invoice
        {
            InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("re 2026 / 0042")),
            IssueDate = FieldValue.Extracted(new DateOnly(2026, 10, 28)),
            Currency = FieldValue.Extracted("EUR"),
            TotalGross = FieldValue.Extracted(new Money(-119.50m, "EUR")),
        };

        return name switch
        {
            "complete" => complete,
            "missing-number" => complete with { InvoiceNumber = FieldValue<DocumentIdentifier>.Missing() },
            "unreadable-total" => complete with { TotalGross = FieldValue<Money>.Unreadable() },
            "lower-case-currency" => complete with { Currency = FieldValue.Extracted("eur") },
            "malformed-currency" => complete with { Currency = FieldValue.Extracted("Euro") },
            "long-number" => complete with { InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create(new string('7', 140))) },
            "conflict" => complete with { IssueDate = FieldValue<DateOnly>.Conflict(new DateOnly(2026, 1, 2)) },
            "corrected" => complete with { TotalGross = complete.TotalGross.Correct(new Money(42m, "EUR"), "reviewer", DateTimeOffset.UnixEpoch) },
            _ => new Invoice(),
        };
    }

    [Theory]
    [MemberData(nameof(Invoices))]
    public void Invoices_project_exactly_as_the_typed_mapping_did(string name)
    {
        var json = CanonicalDocumentSerializer.Serialize(InvoiceCase(name));
        var expected = new Document { TenantId = Guid.Empty, Type = DocumentType.Invoice, CanonicalJson = json };
        LegacyRefresh(expected);

        foreach (var type in new[] { DocumentType.Invoice, DocumentType.CreditNote })
        {
            var actual = new Document { TenantId = Guid.Empty, Type = type, CanonicalJson = json };
            DocumentRoleColumns.Refresh(actual);

            actual.DocumentNumber.ShouldBe(expected.DocumentNumber);
            actual.DocumentNumberNormalized.ShouldBe(expected.DocumentNumberNormalized);
            actual.IssueDate.ShouldBe(expected.IssueDate);
            actual.Currency.ShouldBe(expected.Currency);
            actual.TotalGross.ShouldBe(expected.TotalGross);
        }
    }

    [Fact]
    public void Purchase_orders_project_number_date_currency_and_total()
    {
        var order = new PurchaseOrder
        {
            PoNumber = FieldValue.Extracted(DocumentIdentifier.Create("PO-7")),
            OrderDate = FieldValue.Extracted(new DateOnly(2026, 9, 1)),
            Currency = FieldValue.Extracted("chf"),
            TotalGross = FieldValue.Extracted(new Money(10m, "CHF")),
        };
        var document = new Document { TenantId = Guid.Empty, Type = DocumentType.PurchaseOrder, CanonicalJson = CanonicalDocumentSerializer.Serialize(order) };
        var expected = new Document { TenantId = Guid.Empty, Type = DocumentType.PurchaseOrder, CanonicalJson = document.CanonicalJson };
        LegacyRefresh(expected);

        DocumentRoleColumns.Refresh(document);

        document.DocumentNumber.ShouldBe("PO-7");
        document.IssueDate.ShouldBe(new DateOnly(2026, 9, 1));
        document.Currency.ShouldBe("CHF");
        document.TotalGross.ShouldBe(10m);
        (document.DocumentNumber, document.DocumentNumberNormalized, document.IssueDate, document.Currency, document.TotalGross)
            .ShouldBe((expected.DocumentNumber, expected.DocumentNumberNormalized, expected.IssueDate, expected.Currency, expected.TotalGross));
    }

    [Fact]
    public void A_delivery_note_binds_no_currency_or_total_so_those_columns_are_left_alone()
    {
        var note = new DeliveryNote { DeliveryNoteNumber = FieldValue.Extracted(DocumentIdentifier.Create("LS-9")), DeliveryDate = FieldValue.Extracted(new DateOnly(2026, 8, 8)) };
        var document = new Document
        {
            TenantId = Guid.Empty,
            Type = DocumentType.DeliveryNote,
            CanonicalJson = CanonicalDocumentSerializer.Serialize(note),
            Currency = "EUR",
            TotalGross = 5m,
        };

        DocumentRoleColumns.Refresh(document);

        document.DocumentNumber.ShouldBe("LS-9");
        document.IssueDate.ShouldBe(new DateOnly(2026, 8, 8));
        document.Currency.ShouldBe("EUR");
        document.TotalGross.ShouldBe(5m);
    }

    [Theory]
    [InlineData(DocumentType.Unknown)]
    [InlineData(DocumentType.Statement)]
    [InlineData(DocumentType.Other)]
    public void Types_without_bindings_are_not_touched(DocumentType type)
    {
        var document = new Document { TenantId = Guid.Empty, Type = type, CanonicalJson = "{}", DocumentNumber = "kept", Currency = "EUR" };

        DocumentRoleColumns.Refresh(document);

        document.DocumentNumber.ShouldBe("kept");
        document.Currency.ShouldBe("EUR");
        RoleBindings.ForLegacyType(type).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"invoiceNumber\":\"bare\",\"issueDate\":{\"value\":\"28.10.2026\",\"status\":\"Extracted\"},\"totalGross\":{\"value\":{\"amount\":\"x\"},\"status\":\"Extracted\"}}")]
    public void Malformed_trees_and_leaves_project_as_empty_instead_of_throwing(string? json)
    {
        var projection = RoleProjector.Project(RoleBindings.Invoice, json);

        projection.DocumentNumber.ShouldBeNull();
        projection.DocumentDate.ShouldBeNull();
        projection.Currency.ShouldBeNull();
        projection.Total.ShouldBeNull();
    }

    [Fact]
    public void Generic_leaves_and_nested_paths_are_read_for_definition_driven_trees()
    {
        // The shape a definition-driven document (P1) stores: FieldValue<JsonNode> leaves, possibly in groups.
        var tree = JsonNode.Parse("""
            {
              "header": { "listNumber": { "value": "PL-2026-11", "status": "Extracted" } },
              "validFrom": { "value": "2026-11-01", "status": "Inferred" },
              "currency": { "value": "EUR", "status": "Corrected" },
              "total": { "value": 12.5, "status": "Extracted" }
            }
            """)!.AsObject();

        var projection = RoleProjector.Project(new RoleBindings("header.listNumber", "validFrom", "currency", "total"), tree);

        projection.DocumentNumber.ShouldBe(DocumentIdentifier.Create("PL-2026-11"));
        projection.DocumentDate.ShouldBe(new DateOnly(2026, 11, 1));
        projection.Currency.ShouldBe("EUR");
        projection.Total.ShouldBe(12.5m);
    }

    /// <summary>The mapping <c>DocumentEndpoints.RefreshDenormalizedColumns</c> applied to invoices and purchase orders before the projector.</summary>
    private static void LegacyRefresh(Document document)
    {
        switch (document.Type)
        {
            case DocumentType.Invoice or DocumentType.CreditNote:
                var invoice = CanonicalDocumentSerializer.DeserializeInvoice(document.CanonicalJson!);
                SetNumber(document, invoice.InvoiceNumber.HasValue ? invoice.InvoiceNumber.Value : null);
                document.IssueDate = invoice.IssueDate.HasValue ? invoice.IssueDate.Value : null;
                SetCurrency(document, invoice.Currency.HasValue ? invoice.Currency.Value : null);
                document.TotalGross = invoice.TotalGross.HasValue ? invoice.TotalGross.Value!.Amount : null;
                break;

            case DocumentType.PurchaseOrder:
                var order = CanonicalDocumentSerializer.DeserializePurchaseOrder(document.CanonicalJson!);
                SetNumber(document, order.PoNumber.HasValue ? order.PoNumber.Value : null);
                document.IssueDate = order.OrderDate.HasValue ? order.OrderDate.Value : null;
                SetCurrency(document, order.Currency.HasValue ? order.Currency.Value : null);
                document.TotalGross = order.TotalGross.HasValue ? order.TotalGross.Value!.Amount : null;
                break;
        }

        static void SetNumber(Document document, DocumentIdentifier? number)
        {
            document.DocumentNumber = number is null ? null : Cut(number.Verbatim);
            document.DocumentNumberNormalized = number is null ? null : Cut(number.Normalized);
        }

        static void SetCurrency(Document document, string? currency)
        {
            var normalized = currency is null ? null : CurrencyCode.Normalize(currency);
            document.Currency = CurrencyCode.IsWellFormed(normalized) ? normalized : null;
        }

        static string Cut(string value) => value.Length <= 100 ? value : value[..100];
    }
}
