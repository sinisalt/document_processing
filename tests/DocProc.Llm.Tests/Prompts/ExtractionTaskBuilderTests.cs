using DocProc.Application.Abstractions;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Llm.Prompts;
using DocProc.Llm.Tests.Extraction;

namespace DocProc.Llm.Tests.Prompts;

public class ExtractionTaskBuilderTests
{
    private static readonly DocumentInput PdfDocument = new() { Pdf = Inputs.PdfBytes, PageTexts = ["page one", "page two"] };

    [Fact]
    public void Renders_tenant_context_and_says_a_pdf_is_attached()
    {
        var task = ExtractionTaskBuilder.Build(Inputs.Tenant, null, PdfDocument);

        task.ShouldContain("Legal name: Gasthaus zur Post GmbH");
        task.ShouldContain("VAT id: DE123456789");
        task.ShouldContain("Attached as a PDF (2 pages).");
        task.ShouldNotContain("Supplier memory");
        task.ShouldNotContain("<page");
    }

    [Fact]
    public void Renders_supplier_memory_with_skus_and_a_compact_last_approved_invoice()
    {
        var last = new Invoice
        {
            InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("RE-2026-0001")),
            IssueDate = FieldValue.Extracted(new DateOnly(2026, 9, 30)),
            TotalGross = FieldValue.Extracted(new Money(64.45m, "EUR")),
            Lines = Enumerable.Range(1, 12).Select(i => new InvoiceLine
            {
                LineNumber = i,
                SupplierSku = FieldValue.Extracted($"SKU-{i}"),
                Description = FieldValue.Extracted($"Article {i}"),
                Quantity = FieldValue.Extracted(2.5m),
                Unit = FieldValue.Extracted(UnitOfMeasure.Kilogram),
                UnitRaw = "kg",
                UnitPrice = FieldValue.Extracted(new Money(1.2m, "EUR")),
            }).ToList(),
        };
        var supplier = new SupplierHints(Guid.NewGuid(), "Musterhandel GmbH", "DE000000000", Enumerable.Range(1, 60).Select(i => $"SKU-{i}").ToList(), last, null);

        var task = ExtractionTaskBuilder.Build(Inputs.Tenant, supplier, PdfDocument);

        task.ShouldContain("Matched supplier: Musterhandel GmbH (VAT id DE000000000)");
        task.ShouldContain("Known SKUs (50 of 60): SKU-1, SKU-2");
        task.ShouldNotContain("SKU-51,");
        task.ShouldContain("Last approved invoice: number RE-2026-0001, issued 2026-09-30, total 64.45 EUR, 12 lines");
        task.ShouldContain("  - SKU-1 \"Article 1\" 2.5 kg @ 1.20");
        task.ShouldContain("... and 2 more lines");
        task.ShouldNotContain("SKU-11 \"Article 11\"");
    }

    [Fact]
    public void Describes_page_images_with_their_text_layer()
    {
        var document = new DocumentInput
        {
            PageImages = [new PageImage(1, Inputs.PngBytes, 123, 300)],
            PageTexts = ["Rechnung Nr. 42"],
        };

        var task = ExtractionTaskBuilder.Build(Inputs.Tenant, null, document);

        task.ShouldContain("Attached as 1 page images together with the text layer of each page.");
        task.ShouldNotContain("<page");
    }

    [Fact]
    public void Falls_back_to_inlining_the_text_layer_when_nothing_can_be_attached()
    {
        var document = new DocumentInput { PageTexts = ["Rechnung Nr. 42", "Seite 2"] };

        var task = ExtractionTaskBuilder.Build(Inputs.Tenant, null, document);

        task.ShouldContain("No rendered pages are available.");
        task.ShouldContain("<page number=\"1\">\nRechnung Nr. 42\n</page>");
        task.ShouldContain("<page number=\"2\">\nSeite 2\n</page>");
    }

    [Fact]
    public void Tenant_without_vat_id_omits_the_line()
    {
        var task = ExtractionTaskBuilder.Build(new TenantHints("Pizzeria Luna", null, "EUR"), null, PdfDocument);

        task.ShouldContain("Legal name: Pizzeria Luna");
        task.ShouldNotContain("VAT id:");
    }


    [Fact]
    public void Memory_lines_without_a_printed_unit_show_the_quantity_alone()
    {
        var last = new Invoice
        {
            Lines =
            [
                new InvoiceLine { LineNumber = 1, Description = FieldValue.Extracted("No unit at all"), Quantity = FieldValue.Extracted(2m) },
                new InvoiceLine { LineNumber = 2, Description = FieldValue.Extracted("Other without raw"), Quantity = FieldValue.Extracted(3m), Unit = FieldValue.Extracted(UnitOfMeasure.Other) },
                new InvoiceLine { LineNumber = 3, Description = FieldValue.Extracted("Parsed unit"), Quantity = FieldValue.Extracted(4m), Unit = FieldValue.Extracted(UnitOfMeasure.Crate) },
                new InvoiceLine { LineNumber = 4, Description = FieldValue.Extracted("Raw unit"), Quantity = FieldValue.Extracted(5m), Unit = FieldValue.Extracted(UnitOfMeasure.Other), UnitRaw = "Sack" },
            ],
        };
        var supplier = new SupplierHints(Guid.NewGuid(), "Musterhandel GmbH", null, [], last, null);

        var task = ExtractionTaskBuilder.Build(Inputs.Tenant, supplier, PdfDocument);

        task.ShouldContain("  - \"No unit at all\" 2 \n");
        task.ShouldContain("  - \"Other without raw\" 3 \n");
        task.ShouldNotContain("3 Other");
        task.ShouldContain("  - \"Parsed unit\" 4 Crate \n");
        task.ShouldContain("  - \"Raw unit\" 5 Sack \n");
    }
}
