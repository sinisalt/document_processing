using DocProc.Domain.Documents;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Pdf;
using DocProc.Integration.Tests.Pdf;
using Microsoft.Extensions.Options;

namespace DocProc.Integration.Tests.Classification;

/// <summary>Stage 4 keyword rules (docs/03 "Classify") over generated PDFs and synthetic text layers.</summary>
public class KeywordDocumentClassifierTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly KeywordDocumentClassifier _classifier = new(Options.Create(new KeywordClassifierOptions()));

    private static async Task<TextLayer> ExtractAsync(byte[] pdf)
    {
        var processor = new PdfPigPdfProcessor(Options.Create(new PdfProcessingOptions()));
        return await processor.ExtractTextAsync(TestPdfs.AsStream(pdf), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task German_invoice_fixture_is_an_invoice_in_german_despite_its_delivery_note_reference()
    {
        var layer = await ExtractAsync(TestPdfs.GermanInvoice());

        var outcome = await _classifier.ClassifyAsync(Tenant, layer, null, TestContext.Current.CancellationToken);

        outcome.Type.ShouldBe(DocumentType.Invoice);
        outcome.Source.ShouldBe(TypeSource.Rule);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.8);
        outcome.Confidence.ShouldBeLessThanOrEqualTo(1);
        outcome.Language.ShouldBe(DocumentLanguages.German);
    }

    [Fact]
    public async Task Delivery_note_page_of_the_fixture_is_a_delivery_note_even_though_it_mentions_the_invoice()
    {
        var layer = await ExtractAsync(TestPdfs.InvoiceWithDeliveryNote());
        var deliveryNoteOnly = new TextLayer(layer.Source, [layer.Pages[2]]);

        var outcome = _classifier.Classify(deliveryNoteOnly);

        outcome.Type.ShouldBe(DocumentType.DeliveryNote);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.8);
        outcome.Language.ShouldBe(DocumentLanguages.German);
    }

    [Fact]
    public void German_credit_note_referencing_an_invoice_is_a_credit_note()
    {
        var layer = TextLayers.Layer(TextLayers.Page(
            1,
            (60, "Musterfirma GmbH"),
            (120, "Gutschrift"),
            (150, "Gutschriftsnummer GS-2026-0007"),
            (166, "zur Rechnung Nr. RE-2026-0042 vom 15.03.2026"),
            (182, "Rechnungsdatum 15.03.2026"),
            (300, "Betrag 118,00 EUR MwSt 19 % Summe 140,42 EUR")));

        var outcome = _classifier.Classify(layer);

        outcome.Type.ShouldBe(DocumentType.CreditNote);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.7);
        outcome.Language.ShouldBe(DocumentLanguages.German);
    }

    [Fact]
    public void English_delivery_note_is_detected_with_its_language()
    {
        var layer = TextLayers.Layer(TextLayers.Page(
            1,
            (60, "Acme Supplies Ltd"),
            (110, "Delivery note"),
            (150, "Delivery note no. DN-5512"),
            (166, "Your purchase order PO-889 of 2 March 2026"),
            (300, "Description Quantity"),
            (316, "Bolt M8 100"),
            (332, "The invoice will follow by email.")));

        var outcome = _classifier.Classify(layer);

        outcome.Type.ShouldBe(DocumentType.DeliveryNote);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.7);
        outcome.Language.ShouldBe(DocumentLanguages.English);
    }

    [Theory]
    [InlineData("Račun br. 12/1/1", "Datum računa 03.04.2026 Ukupno 1.250,00 EUR PDV 25 % Iznos za plaćanje", DocumentLanguages.Croatian)]
    [InlineData("Fattura", "Numero fattura 2026/118 Totale importo 1.250,00 EUR IVA 22 % Pagamento 30 giorni", DocumentLanguages.Italian)]
    [InlineData("Facture", "Numéro de facture F-2026-118 Montant total 1 250,00 EUR TVA 20 % Paiement à 30 jours", DocumentLanguages.French)]
    [InlineData("Invoice", "Invoice number INV-2026-118 Total amount due 1,250.00 EUR VAT 20 % Payment terms 30 days", DocumentLanguages.English)]
    public void Invoices_in_other_languages_are_detected_with_their_language(string title, string body, string language)
    {
        var layer = TextLayers.Layer(TextLayers.Page(1, (60, "Supplier d.o.o."), (120, title), (160, body)));

        var outcome = _classifier.Classify(layer);

        outcome.Type.ShouldBe(DocumentType.Invoice);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.6);
        outcome.Language.ShouldBe(language);
    }

    [Theory]
    [InlineData("Kontoauszug", DocumentType.Statement)]
    [InlineData("Mahnung", DocumentType.Reminder)]
    [InlineData("Zahlungserinnerung", DocumentType.Reminder)]
    [InlineData("Bestellung", DocumentType.PurchaseOrder)]
    [InlineData("Purchase order", DocumentType.PurchaseOrder)]
    [InlineData("Statement of account", DocumentType.Statement)]
    [InlineData("Opomena", DocumentType.Reminder)]
    [InlineData("Nota di credito", DocumentType.CreditNote)]
    [InlineData("Bon de livraison", DocumentType.DeliveryNote)]
    public void Title_in_the_header_decides_the_type_even_when_invoices_are_listed_below(string title, DocumentType expected)
    {
        var layer = TextLayers.Layer(TextLayers.Page(
            1,
            (60, "Musterfirma GmbH"),
            (120, title),
            (400, "Rechnung Nr. RE-2026-0001 vom 01.02.2026 100,00 EUR"),
            (416, "Rechnung Nr. RE-2026-0002 vom 05.02.2026 250,00 EUR"),
            (432, "Rechnung Nr. RE-2026-0003 vom 09.02.2026 75,00 EUR")));

        var outcome = _classifier.Classify(layer);

        outcome.Type.ShouldBe(expected);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.6);
    }

    [Fact]
    public void A_single_mention_on_a_later_page_is_below_threshold_and_returns_unknown()
    {
        var layer = TextLayers.Layer(
            TextLayers.Page(1, (120, "Hiermit übersenden wir Ihnen die gewünschten Unterlagen."), (300, "Mit freundlichen Grüßen")),
            TextLayers.Page(2, (500, "Hinweis: Invoice wird separat versendet.")));

        var outcome = _classifier.Classify(layer);

        outcome.Type.ShouldBe(DocumentType.Unknown);
        outcome.Source.ShouldBe(TypeSource.Rule);
        outcome.Confidence.ShouldBeGreaterThan(0);
        outcome.Confidence.ShouldBeLessThan(0.6);
    }

    [Fact]
    public void The_threshold_is_configurable()
    {
        var lenient = new KeywordDocumentClassifier(Options.Create(new KeywordClassifierOptions { ConfidenceThreshold = 0.1 }));
        var layer = TextLayers.Layer(TextLayers.Page(1, (500, "Hinweis: Invoice wird separat versendet.")));

        var strict = _classifier.Classify(layer);
        var relaxed = lenient.Classify(layer);

        strict.Type.ShouldBe(DocumentType.Unknown);
        relaxed.Type.ShouldBe(DocumentType.Invoice);
        relaxed.Confidence.ShouldBe(strict.Confidence);
    }

    [Fact]
    public void Negative_evidence_outweighs_a_stray_title()
    {
        var layer = TextLayers.Layer(TextLayers.Page(
            1,
            (120, "Rechnung"),
            (150, "Dies ist keine Rechnung. Proforma zur Vorlage beim Zoll."),
            (300, "Gesamtbetrag 1.000,00 EUR")));

        var outcome = _classifier.Classify(layer);

        outcome.Type.ShouldBe(DocumentType.Unknown);
    }

    [Fact]
    public void A_single_header_negation_defeats_the_title()
    {
        // A cover letter headed "Rechnung" that says the invoice follows: one explicit negation in the header offsets the title.
        var layer = TextLayers.Layer(TextLayers.Page(
            1,
            (60, "Musterfirma GmbH"),
            (120, "Rechnung"),
            (150, "Rechnungsnummer RE-1"),
            (180, "Rechnung folgt nach Lieferung."),
            (300, "Mit freundlichen Grüßen")));

        var outcome = _classifier.Classify(layer);

        outcome.Type.ShouldBe(DocumentType.Unknown);
        outcome.Confidence.ShouldBeLessThan(0.6);
    }

    [Fact]
    public void Proforma_is_one_piece_of_evidence_whichever_languages_list_it()
    {
        var proforma = _classifier.Classify(TextLayers.Layer(TextLayers.Page(
            1,
            (120, "Proforma Invoice"),
            (150, "Invoice number PI-1"),
            (166, "Invoice date 15.03.2026"),
            (300, "Total 1,000.00 EUR"))));
        var body = _classifier.Classify(TextLayers.Layer(TextLayers.Page(
            1,
            (120, "Invoice"),
            (150, "Invoice number INV-1"),
            (166, "Invoice date 15.03.2026"),
            (500, "Replaces proforma PI-1 of 1 March 2026"))));

        // Header title (8) and labels (4) against one header negation (-8): not enough for a confident invoice.
        proforma.Type.ShouldBe(DocumentType.Unknown);
        proforma.Confidence.ShouldBe(0.5);

        // The same word in the body (-4) does not overturn a clear invoice header.
        body.Type.ShouldBe(DocumentType.Invoice);
        body.Confidence.ShouldBe(1);
    }

    [Fact]
    public void Invoice_referencing_its_delivery_note_and_purchase_order_in_the_body_is_an_invoice()
    {
        var layer = TextLayers.Layer(TextLayers.Page(
            1,
            (60, "Acme Supplies Ltd"),
            (120, "INVOICE"),
            (150, "Invoice number INV-1"),
            (166, "Invoice date 15 March 2026"),
            (320, "Delivery note DN-5 of 10 March 2026"),
            (336, "Purchase order PO-7"),
            (352, "Statement attached for your records"),
            (500, "Total amount due 1,250.00 EUR")));

        var outcome = _classifier.Classify(layer);

        outcome.Type.ShouldBe(DocumentType.Invoice);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.6);
        outcome.Language.ShouldBe(DocumentLanguages.English);
    }

    [Fact]
    public void Text_without_keywords_or_without_a_text_layer_is_unknown_with_zero_confidence()
    {
        var noKeywords = _classifier.Classify(TextLayers.Layer(TextLayers.Page(1, (120, "Lorem ipsum dolor sit amet"), (140, "consectetur adipiscing elit"))));
        var empty = _classifier.Classify(TextLayers.Layer(TextLayers.BlankPage(1)));
        var none = _classifier.Classify(null);

        noKeywords.Type.ShouldBe(DocumentType.Unknown);
        noKeywords.Confidence.ShouldBe(0);
        noKeywords.Language.ShouldBeNull();
        empty.Type.ShouldBe(DocumentType.Unknown);
        empty.Confidence.ShouldBe(0);
        none.Type.ShouldBe(DocumentType.Unknown);
        none.Confidence.ShouldBe(0);
        none.Source.ShouldBe(TypeSource.Rule);
    }

    [Fact]
    public void Keywords_inside_compounds_do_not_count_as_titles()
    {
        // "Rechnungsadresse" and "Lieferscheinnummer" contain titles but are an address label and a number label.
        var layer = TextLayers.Layer(TextLayers.Page(
            1,
            (120, "Lieferschein"),
            (150, "Rechnungsadresse: Musterkunde AG, Musterweg 1, 10115 Berlin"),
            (166, "Lieferscheinnummer LS-1")));

        var outcome = _classifier.Classify(layer);

        outcome.Type.ShouldBe(DocumentType.DeliveryNote);
        outcome.Confidence.ShouldBeGreaterThanOrEqualTo(0.8);
    }

    [Fact]
    public async Task Classify_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _classifier.ClassifyAsync(Tenant, null, null, cts.Token));
    }
}
