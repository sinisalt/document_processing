using DocProc.Application.Abstractions;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Llm.Extraction;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests.Extraction;

public class LlmInvoiceExtractorTests
{
    private static readonly PromptCatalogue Prompts = PromptCatalogue.Load();

    private static LlmInvoiceExtractor Extractor(FakeGateway gateway, LlmExtractionOptions? options = null) =>
        new(gateway, Prompts, PriceTable.Default, options ?? new LlmExtractionOptions());

    [Fact]
    public void Handles_invoices_and_credit_notes_with_any_readable_input_only()
    {
        var extractor = Extractor(new FakeGateway());

        extractor.Method.ShouldBe(ExtractionMethod.Llm);
        extractor.CanHandle(Inputs.Extraction()).ShouldBeTrue();
        extractor.CanHandle(Inputs.Extraction(DocumentType.CreditNote)).ShouldBeTrue();
        extractor.CanHandle(Inputs.Extraction(DocumentType.PurchaseOrder)).ShouldBeFalse();
        extractor.CanHandle(Inputs.Extraction(pdf: false, textLayer: new TextLayerBuilder().Page(1).Build())).ShouldBeFalse();
        extractor.CanHandle(Inputs.Extraction(pdf: false, images: [Inputs.PngBytes], textLayer: new TextLayerBuilder().Page(1).Build())).ShouldBeTrue();
    }

    [Fact]
    public async Task Unsupported_input_returns_null_without_calling_the_gateway()
    {
        var gateway = new FakeGateway();

        var outcome = await Extractor(gateway).ExtractAsync(Inputs.Extraction(DocumentType.DeliveryNote), CancellationToken.None);

        outcome.ShouldBeNull();
        gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Builds_an_extract_request_with_the_pdf_the_schema_and_the_rendered_task()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Invoice);
        var supplier = new SupplierHints(Guid.NewGuid(), "METRO Deutschland GmbH", "DE811177936", ["123"], null, null);

        await Extractor(gateway, new LlmExtractionOptions { MaxOutputTokens = 4_000, Effort = ReasoningEffort.Medium })
            .ExtractAsync(Inputs.Extraction(supplier: supplier), CancellationToken.None);

        var request = gateway.Requests.ShouldHaveSingleItem();
        request.Stage.ShouldBe(LlmStage.Extract);
        request.Model.ShouldBe(LlmRequestDefaults.ModelResolvedByGateway);
        request.OutputSchema.ShouldBe(CanonicalSchemas.InvoiceExtraction);
        request.DocumentId.ShouldBe(Inputs.DocumentId);
        request.Options.MaxOutputTokens.ShouldBe(4_000);
        request.Options.Effort.ShouldBe(ReasoningEffort.Medium);
        request.Prompt.CacheKey.ShouldBe(Prompts.ExtractInvoice.CacheKey);
        request.Prompt.Examples.Count.ShouldBe(2);
        request.Prompt.Task.ShouldContain("Legal name: Gasthaus zur Post GmbH");
        request.Prompt.Task.ShouldContain("Matched supplier: METRO Deutschland GmbH");
        request.Document.ShouldNotBeNull();
        request.Document.HasPdf.ShouldBeTrue();
        request.Document.FileName.ShouldBe($"{Inputs.DocumentId:N}.pdf");
        request.Document.PageTexts.Count.ShouldBe(2);
        request.Document.PageTexts[0].ShouldContain("RE-2026-0042");
    }

    [Fact]
    public async Task Without_a_pdf_page_images_carry_their_dimensions_and_the_text_layer_rides_along()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Invoice);

        await Extractor(gateway).ExtractAsync(Inputs.Extraction(pdf: false, images: [Inputs.PngBytes, Inputs.PngBytes]), CancellationToken.None);

        var document = gateway.Requests.Single().Document.ShouldNotBeNull();
        document.HasPdf.ShouldBeFalse();
        document.PageImages.Count.ShouldBe(2);
        document.PageImages[0].ShouldSatisfyAllConditions(p => p.Page.ShouldBe(1), p => p.Width.ShouldBe(123), p => p.Height.ShouldBe(300));
        document.PageImages[1].Page.ShouldBe(2);
        document.PageTexts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Text_only_input_inlines_the_text_layer_in_the_task_and_attaches_nothing()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Invoice);

        await Extractor(gateway).ExtractAsync(Inputs.Extraction(pdf: false), CancellationToken.None);

        var request = gateway.Requests.Single();
        request.Document.ShouldBeNull();
        request.Prompt.Task.ShouldContain("No rendered pages are available.");
        request.Prompt.Task.ShouldContain("<page number=\"1\">");
        request.Prompt.Task.ShouldContain("RE-2026-0042");
    }

    [Fact]
    public async Task Maps_the_answer_onto_the_canonical_invoice_with_located_provenance()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Invoice);

        var outcome = (await Extractor(gateway).ExtractAsync(Inputs.Extraction(), CancellationToken.None)).ShouldNotBeNull();

        outcome.Method.ShouldBe(ExtractionMethod.Llm);
        var invoice = outcome.Invoice;

        invoice.Kind.Value.ShouldBe(InvoiceKind.Invoice);
        invoice.Kind.Source!.RawText.ShouldBe("Rechnung");

        var number = invoice.InvoiceNumber;
        number.Status.ShouldBe(FieldStatus.Extracted);
        number.Value!.Verbatim.ShouldBe("RE-2026-0042");
        number.Value.Normalized.ShouldBe("RE-2026-0042");
        number.Source.ShouldBe(new FieldSource(SourceMethod.Llm, 1, new BoundingBox(180, 100, 300, 115), "RE-2026-0042"));
        number.Confidence.ShouldBe(LocatabilityConfidence.Exact);

        var total = invoice.TotalGross;
        total.Value.ShouldBe(new Money(166.50m, "EUR"));
        total.Source.ShouldBe(new FieldSource(SourceMethod.Llm, 1, new BoundingBox(770, 900, 830, 915), "166,50"));
        total.Confidence.ShouldBe(LocatabilityConfidence.Exact);

        invoice.IssueDate.Value.ShouldBe(new DateOnly(2026, 10, 27));
        invoice.IssueDate.Source!.RawText.ShouldBe("27.10.2026");
        invoice.Currency.ShouldSatisfyAllConditions(
            c => c.Status.ShouldBe(FieldStatus.Extracted),
            c => c.Value.ShouldBe("EUR"),
            c => c.Confidence.ShouldBe(LocatabilityConfidence.Exact));
        invoice.Customer.Status.ShouldBe(FieldStatus.Missing);
        invoice.OrderReferences.ShouldHaveSingleItem().Value!.Verbatim.ShouldBe("PO-77");
        invoice.DeliveryNoteReferences.ShouldBeEmpty();
        invoice.SubtotalNet.Value.ShouldBe(new Money(150m, "EUR"));
        invoice.TotalTax.Value.ShouldBe(new Money(16.50m, "EUR"));
        invoice.AmountDue.Status.ShouldBe(FieldStatus.Missing);
        invoice.PaymentTerms.Status.ShouldBe(FieldStatus.Missing);

        var tax = invoice.TaxBreakdown.ShouldHaveSingleItem();
        tax.Rate.ShouldBe(7m);
        tax.TaxableAmount.ShouldBe(new Money(100m, "EUR"));
        tax.TaxAmount.ShouldBe(new Money(7m, "EUR"));
        tax.Category.ShouldBe(TaxCategory.Standard);

        var supplier = invoice.Supplier;
        supplier.Value!.Name.ShouldBe("METRO Deutschland GmbH");
        supplier.Value.VatId.ShouldBe("DE811177936");
        supplier.Value.Address!.CountryCode.ShouldBe("DE");
        supplier.Source!.Page.ShouldBe(1);
        supplier.Source.RawText.ShouldBe("METRO Deutschland GmbH");

        var line = invoice.Lines.ShouldHaveSingleItem();
        line.LineNumber.ShouldBe(1);
        line.SupplierSku.Value.ShouldBe("123");
        line.Description.Value.ShouldBe("Tomaten");
        line.Quantity.Value.ShouldBe(10m);
        line.Quantity.Source!.RawText.ShouldBe("10,00");
        line.Unit.Value.ShouldBe(UnitOfMeasure.Kilogram);
        line.UnitRaw.ShouldBe("kg");
        line.UnitPrice.Value.ShouldBe(new Money(10m, "EUR"));
        line.LineNet.Value.ShouldBe(new Money(100m, "EUR"));
        line.TaxRate.Value.ShouldBe(7m);
        line.Gtin.Status.ShouldBe(FieldStatus.Missing);
        foreach (var (path, field) in invoice.EnumerateAllFields().Where(f => f.Field.HasValue))
        {
            field.Source!.Method.ShouldBe(SourceMethod.Llm, path);
        }
    }

    [Fact]
    public async Task Confidence_follows_locatability_not_the_model()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Invoice);

        var invoice = (await Extractor(gateway).ExtractAsync(Inputs.Extraction(), CancellationToken.None))!.Invoice;

        invoice.InvoiceNumber.Confidence.ShouldBe(LocatabilityConfidence.Exact);
        // VAT id is printed as "DE 811177936": a whitespace-normalised match, the weaker of name and VAT id.
        invoice.Supplier.Confidence.ShouldBe(LocatabilityConfidence.Normalized);
        // "Tomaten" is printed as part of "Strauchtomaten".
        invoice.Lines[0].Description.Confidence.ShouldBe(LocatabilityConfidence.Fuzzy);
        // The due date is nowhere on the page.
        invoice.DueDate.ShouldSatisfyAllConditions(
            d => d.Value.ShouldBe(new DateOnly(2026, 11, 10)),
            d => d.Confidence.ShouldBe(LocatabilityConfidence.NotFound),
            d => d.Source.ShouldBe(new FieldSource(SourceMethod.Llm)));
    }

    [Fact]
    public async Task Without_a_text_layer_every_value_is_unlocated_at_the_not_found_confidence()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Invoice);

        var invoice = (await Extractor(gateway).ExtractAsync(Inputs.Extraction(textLayer: new TextLayerBuilder().Page(1).Build()), CancellationToken.None))!.Invoice;

        invoice.InvoiceNumber.Confidence.ShouldBe(LocatabilityConfidence.NotFound);
        invoice.TotalGross.Source!.Page.ShouldBeNull();
    }

    [Fact]
    public async Task Null_currency_is_inferred_from_the_tenant_default_and_nothing_else_is()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Invoice.Replace("\"currency\": \"EUR\"", "\"currency\": null", StringComparison.Ordinal));

        var invoice = (await Extractor(gateway).ExtractAsync(Inputs.Extraction(tenant: Inputs.Tenant with { DefaultCurrency = "chf" }), CancellationToken.None))!.Invoice;

        invoice.Currency.Status.ShouldBe(FieldStatus.Inferred);
        invoice.Currency.Value.ShouldBe("CHF");
        invoice.Currency.Confidence.ShouldBeNull();
        invoice.TotalGross.Value.Currency.ShouldBe("CHF");
        invoice.Lines[0].UnitPrice.Value.Currency.ShouldBe("CHF");
        invoice.TaxBreakdown[0].TaxAmount.Currency.ShouldBe("CHF");
        invoice.EnumerateAllFields().Count(f => f.Field.Status == FieldStatus.Inferred).ShouldBe(1);
    }

    [Fact]
    public async Task Unreadable_regions_become_blocking_unreadable_pages_flags_with_page_and_area()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Invoice);

        var outcome = (await Extractor(gateway).ExtractAsync(Inputs.Extraction(), CancellationToken.None))!;

        var flag = outcome.Flags.ShouldHaveSingleItem();
        flag.Code.ShouldBe(FlagCodes.UnreadablePages);
        flag.Severity.ShouldBe(FlagCodes.Catalogue[FlagCodes.UnreadablePages].DefaultSeverity);
        flag.Message.ShouldBe("Page 2, stamp over totals: ink smudge");
        flag.CreatedBy.ShouldBe(FlagOrigin.Pipeline);
    }

    [Fact]
    public async Task Cost_comes_from_the_gateway_usage_and_the_price_table()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Invoice, usage: new LlmUsage(10_000, 1_000, CacheReadTokens: 2_000));

        var outcome = (await Extractor(gateway).ExtractAsync(Inputs.Extraction(), CancellationToken.None))!;

        outcome.Cost.LlmInputTokens.ShouldBe(12_000);
        outcome.Cost.LlmOutputTokens.ShouldBe(1_000);
        outcome.Cost.OcrPages.ShouldBe(0);
        outcome.Cost.LlmModel.ShouldBe("claude-opus-5-5");
        outcome.Cost.Usd.ShouldBe(0.0608m);
    }

    [Fact]
    public async Task Truncated_answer_is_retried_once_with_twice_the_output_budget()
    {
        var gateway = new FakeGateway()
            .Answer("{\"kind\":\"Invoice\",\"invoiceNumber\":\"RE-", StopReasons.MaxTokens, new LlmUsage(5_000, 8_192))
            .Answer(SampleAnswers.Invoice, usage: new LlmUsage(5_000, 1_000));

        var outcome = (await Extractor(gateway, new LlmExtractionOptions { MaxOutputTokens = 8_192 }).ExtractAsync(Inputs.Extraction(), CancellationToken.None))!;

        gateway.Requests.Count.ShouldBe(2);
        gateway.Requests[0].Options.MaxOutputTokens.ShouldBe(8_192);
        gateway.Requests[1].Options.MaxOutputTokens.ShouldBe(16_384);
        gateway.Requests[1].Prompt.ShouldBe(gateway.Requests[0].Prompt);
        outcome.Invoice.InvoiceNumber.Value!.Verbatim.ShouldBe("RE-2026-0042");
        outcome.Cost.LlmInputTokens.ShouldBe(10_000, "both attempts are billed");
        outcome.Cost.LlmOutputTokens.ShouldBe(9_192);
    }

    [Fact]
    public async Task Truncated_twice_fails_with_a_clear_message()
    {
        var gateway = new FakeGateway()
            .Answer("{\"kind\":", StopReasons.MaxTokens)
            .Answer("{\"kind\":\"Invoice\",", StopReasons.MaxTokens);

        var ex = await Should.ThrowAsync<LlmExtractionException>(() => Extractor(gateway).ExtractAsync(Inputs.Extraction(), CancellationToken.None));

        ex.Message.ShouldContain("truncated twice");
        ex.Message.ShouldContain("8192, then 16384");
        gateway.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Provider_faults_propagate_so_the_pipeline_decides()
    {
        var gateway = new FakeGateway().Fail(new LlmException(LlmErrorKind.Quota, "Monthly cap reached."));

        var ex = await Should.ThrowAsync<LlmException>(() => Extractor(gateway).ExtractAsync(Inputs.Extraction(), CancellationToken.None));

        ex.Kind.ShouldBe(LlmErrorKind.Quota);
        ex.BlocksTenant.ShouldBeTrue();
        gateway.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Refusal_is_a_content_refused_fault_and_malformed_json_is_an_extraction_failure()
    {
        var refusing = new FakeGateway().Answer("I cannot help with that.", StopReasons.Refusal);
        var malformed = new FakeGateway().Answer("{\"kind\": \"Invoice\", \"lines\": \"not an array\"}");

        var refusal = await Should.ThrowAsync<LlmException>(() => Extractor(refusing).ExtractAsync(Inputs.Extraction(), CancellationToken.None));
        var failure = await Should.ThrowAsync<LlmExtractionException>(() => Extractor(malformed).ExtractAsync(Inputs.Extraction(), CancellationToken.None));

        refusal.Kind.ShouldBe(LlmErrorKind.ContentRefused);
        failure.Message.ShouldContain("invoice_extraction@1");
        failure.InnerException.ShouldBeOfType<System.Text.Json.JsonException>();
    }

    [Fact]
    public void Mapper_normalises_tax_categories_units_and_skips_rows_it_cannot_represent()
    {
        InvoiceExtractionMapper.TaxCategoryFor("AE").ShouldBe(TaxCategory.ReverseCharge);
        InvoiceExtractionMapper.TaxCategoryFor(" k ").ShouldBe(TaxCategory.IntraCommunity);
        InvoiceExtractionMapper.TaxCategoryFor("X").ShouldBeNull();
        InvoiceExtractionMapper.TaxCategoryFor(null).ShouldBeNull();

        var mapper = new InvoiceExtractionMapper(ProvenanceLocator.Empty, "EUR");
        var result = mapper.Map(new Contracts.InvoiceExtraction
        {
            Currency = "usd",
            OrderReferences = [" ", "PO-1"],
            DeliveryNoteReferences = [],
            TaxBreakdown = [new Contracts.ExtractedTaxRow { RatePercent = 19, TaxableAmount = null, TaxAmount = 3.8m }],
            ChargesAndAllowances =
            [
                new Contracts.ExtractedChargeOrAllowance { Description = "Freight", Amount = 5m, TaxRatePercent = 19, IsCharge = true },
                new Contracts.ExtractedChargeOrAllowance { Description = "Skonto", Amount = 2m, IsCharge = false },
                new Contracts.ExtractedChargeOrAllowance { Description = "Unknown", Amount = null, IsCharge = true },
            ],
            PaymentTerms = new Contracts.ExtractedPaymentTerms { DueDate = new DateOnly(2026, 12, 1), RawText = "30 days net" },
            ServicePeriodFrom = new DateOnly(2026, 10, 1),
            ServicePeriodTo = new DateOnly(2026, 10, 31),
            Lines =
            [
                new Contracts.ExtractedInvoiceLine { LineNumber = 0, Unit = "Kiste", Quantity = 3, DepositAmount = 9.9m, LineNet = 9.9m },
                new Contracts.ExtractedInvoiceLine { LineNumber = 2, Unit = "Sack", Quantity = 1 },
            ],
            UnreadableRegions = [],
        });

        var invoice = result.Invoice;
        invoice.Currency.Value.ShouldBe("USD");
        invoice.OrderReferences.ShouldHaveSingleItem().Value!.Verbatim.ShouldBe("PO-1");
        invoice.TaxBreakdown.ShouldBeEmpty("a row without a taxable base cannot be a breakdown line");
        invoice.Charges.ShouldHaveSingleItem().ShouldBe(new ChargeOrAllowance("Freight", new Money(5m, "USD"), 19, true));
        invoice.Allowances.ShouldHaveSingleItem().Amount.ShouldBe(new Money(2m, "USD"));
        invoice.PaymentTerms.Value.ShouldBe(new PaymentTerms(new DateOnly(2026, 12, 1), RawText: "30 days net"));
        invoice.ServicePeriod.Value.ShouldBe(new DatePeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        invoice.Lines[0].LineNumber.ShouldBe(1, "a non-positive line number falls back to the position");
        invoice.Lines[0].Unit.Value.ShouldBe(UnitOfMeasure.Crate);
        invoice.Lines[0].UnitRaw.ShouldBe("Kiste");
        invoice.Lines[0].DepositAmount.Value.ShouldBe(new Money(9.9m, "USD"));
        invoice.Lines[1].Unit.Value.ShouldBe(UnitOfMeasure.Other);
        invoice.Lines[1].UnitRaw.ShouldBe("Sack");
        result.Flags.ShouldBeEmpty();
    }
}
