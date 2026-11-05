using DocProc.Llm;

namespace DocProc.Pipeline.Tests.Demo;

/// <summary>
/// The hand-authored LLM answers behind the demo replay fixtures (docs/09 "Demo data"): what a good model returns for
/// the two demo PDFs of <see cref="Pdf.DemoPdfs"/>. They are written in the structured-output contracts of
/// <c>DocProc.Llm.Contracts</c> and parsed by the real stage code when the fixtures are captured, so a schema change
/// that breaks them fails the capture test.
/// </summary>
internal static class DemoAnswers
{
    /// <summary>
    /// Invoice 2026-131 as printed, except the due date, which the model worked out from "Zahlbar innerhalb von 14 Tagen"
    /// and which is therefore nowhere in the text layer: stage 6 gives it the not-found confidence and stage 7 raises
    /// LOW_CONFIDENCE, so the demo has a PDF in the review queue with a field to check.
    /// </summary>
    public const string InvoiceExtraction = """
        {
          "kind": "Invoice", "invoiceNumber": "2026-131", "issueDate": "2026-10-28", "deliveryDate": "2026-10-28",
          "servicePeriodFrom": null, "servicePeriodTo": null, "dueDate": "2026-11-11", "currency": "EUR",
          "supplier": { "name": "Bio-Hof Müller", "vatId": "DE287654321", "taxId": "156/220/40817", "registrationNumber": null,
                        "addressLine1": "Dorfstraße 4", "addressLine2": null, "postalCode": "83135", "city": "Schechen", "countryCode": "DE",
                        "email": "hof@biohof-mueller.example", "phone": "08039 904127", "iban": "DE20 7016 9165 0000 1234 56", "bic": "GENODEF1VRR" },
          "customer": { "name": "Gasthaus zur Post GmbH", "vatId": null, "taxId": null, "registrationNumber": null,
                        "addressLine1": "Hauptstraße 12", "addressLine2": "Küche / Wareneingang", "postalCode": "83022", "city": "Rosenheim", "countryCode": "DE",
                        "email": null, "phone": null, "iban": null, "bic": null },
          "orderReferences": [], "deliveryNoteReferences": [], "originalInvoiceReference": null,
          "subtotalNet": 431.40,
          "taxBreakdown": [ { "ratePercent": 7, "taxableAmount": 431.40, "taxAmount": 30.20, "categoryCode": "S" } ],
          "totalTax": 30.20, "totalGross": 461.60, "amountDue": 461.60, "rounding": null,
          "chargesAndAllowances": [],
          "paymentTerms": { "dueDate": "2026-11-11", "discountPercent": null, "discountUntil": null, "paymentReference": "2026-131",
                            "iban": "DE20 7016 9165 0000 1234 56", "bic": "GENODEF1VRR", "rawText": "Zahlbar innerhalb von 14 Tagen ohne Abzug." },
          "notes": "Alle Erzeugnisse aus kontrolliert biologischem Anbau, Öko-Kontrollstelle DE-ÖKO-006.",
          "lines": [
            { "lineNumber": 1, "supplierSku": null, "gtin": null, "description": "Eier Freiland Größe M, 30 Stück", "quantity": 8, "unit": "Pck", "packSize": "30 Stück",
              "unitPrice": 9.60, "discountPercent": null, "discountAmount": null, "lineNet": 76.80, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 2, "supplierSku": null, "gtin": null, "description": "Bergkäse 12 Monate gereift", "quantity": 2.4, "unit": "kg", "packSize": null,
              "unitPrice": 24.50, "discountPercent": null, "discountAmount": null, "lineNet": 58.80, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 3, "supplierSku": null, "gtin": null, "description": "Bauernbutter 250 g", "quantity": 24, "unit": "Stk", "packSize": "250 g",
              "unitPrice": 2.95, "discountPercent": null, "discountAmount": null, "lineNet": 70.80, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 4, "supplierSku": null, "gtin": null, "description": "Speisekartoffeln festkochend", "quantity": 50, "unit": "kg", "packSize": null,
              "unitPrice": 1.40, "discountPercent": null, "discountAmount": null, "lineNet": 70.00, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 5, "supplierSku": null, "gtin": null, "description": "Karotten", "quantity": 10, "unit": "kg", "packSize": null,
              "unitPrice": 1.90, "discountPercent": null, "discountAmount": null, "lineNet": 19.00, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 6, "supplierSku": null, "gtin": null, "description": "Rote Bete gekocht, vakuumiert", "quantity": 5, "unit": "kg", "packSize": null,
              "unitPrice": 3.20, "discountPercent": null, "discountAmount": null, "lineNet": 16.00, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 7, "supplierSku": null, "gtin": null, "description": "Apfelsaft naturtrüb 1 l", "quantity": 12, "unit": "Fl", "packSize": "1 l",
              "unitPrice": 2.80, "discountPercent": null, "discountAmount": null, "lineNet": 33.60, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 8, "supplierSku": null, "gtin": null, "description": "Bio-Joghurt natur 500 g", "quantity": 20, "unit": "Becher", "packSize": "500 g",
              "unitPrice": 1.65, "discountPercent": null, "discountAmount": null, "lineNet": 33.00, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 9, "supplierSku": null, "gtin": null, "description": "Blütenhonig 500 g", "quantity": 6, "unit": "Glas", "packSize": "500 g",
              "unitPrice": 6.90, "discountPercent": null, "discountAmount": null, "lineNet": 41.40, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 2 },
            { "lineNumber": 10, "supplierSku": null, "gtin": null, "description": "Lieferpauschale Rosenheim", "quantity": 1, "unit": "pauschal", "packSize": null,
              "unitPrice": 12.00, "discountPercent": null, "discountAmount": null, "lineNet": 12.00, "taxRatePercent": 7, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 2 }
          ],
          "unreadableRegions": []
        }
        """;

    /// <summary>The customer number Bio-Hof Müller prints on invoice 2026-131 ("Kundennummer 0815").</summary>
    public const string CustomerNumber = "0815";

    /// <summary>
    /// The same invoice read against the tenant's fork of Invoice with the demo custom field of
    /// <c>fixtures/demo/types/invoice-customer-number.field.json</c> (docs/09 "Document types"): the answer with
    /// <c>x_custom</c>, so the reprocessed document stores the customer number as a field.
    /// </summary>
    public static string InvoiceExtractionWithCustomField
    {
        get
        {
            var answer = System.Text.Json.Nodes.JsonNode.Parse(InvoiceExtraction)!.AsObject();
            answer["x_custom"] = new System.Text.Json.Nodes.JsonObject { ["supplierCustomerRef"] = CustomerNumber };
            return answer.ToJsonString();
        }
    }

    /// <summary>
    /// Credit note GS-2026-0918 of Getränke Hoffmann (<see cref="Pdf.DemoPdfs.CreditNote"/>) read against the tenant's
    /// fork of CreditNote with the custom field of docs/11 §5.3 (<c>fixtures/demo/types/credit-note-supplier-customer-ref.field.json</c>,
    /// docs/09 "Demo: types as data"), as printed except the due date, which the model worked out from "spätestens nach
    /// 30 Tagen ausgezahlt": it is nowhere in the text layer, so stage 7 raises LOW_CONFIDENCE and the credit note goes
    /// to review, where the reviewer sees the custom field in the generated form.
    /// </summary>
    public const string CreditNoteExtractionWithCustomField = """
        {
          "kind": "CreditNote", "invoiceNumber": "GS-2026-0918", "issueDate": "2026-11-02", "deliveryDate": "2026-10-27",
          "servicePeriodFrom": null, "servicePeriodTo": null, "dueDate": "2026-12-02", "currency": "EUR",
          "supplier": { "name": "Getränke Hoffmann GmbH", "vatId": "DE298765432", "taxId": "156/118/40233", "registrationNumber": "HRB 21877",
                        "addressLine1": "Brauhausweg 9", "addressLine2": null, "postalCode": "83026", "city": "Rosenheim", "countryCode": "DE",
                        "email": "rechnung@getraenke-hoffmann.de", "phone": "08031 4077-0", "iban": "DE61 7115 0000 0020 4711 08", "bic": "BYLADEM1ROS" },
          "customer": { "name": "Gasthaus zur Post GmbH", "vatId": null, "taxId": null, "registrationNumber": null,
                        "addressLine1": "Hauptstraße 12", "addressLine2": null, "postalCode": "83022", "city": "Rosenheim", "countryCode": "DE",
                        "email": null, "phone": null, "iban": null, "bic": null },
          "orderReferences": [], "deliveryNoteReferences": [], "originalInvoiceReference": "RE-2026-55318",
          "subtotalNet": 93.20,
          "taxBreakdown": [ { "ratePercent": 19, "taxableAmount": 93.20, "taxAmount": 17.71, "categoryCode": "S" } ],
          "totalTax": 17.71, "totalGross": 110.91, "amountDue": 110.91, "rounding": null,
          "chargesAndAllowances": [],
          "paymentTerms": { "dueDate": "2026-12-02", "discountPercent": null, "discountUntil": null, "paymentReference": null,
                            "iban": null, "bic": null, "rawText": "Den Betrag verrechnen wir mit unserer nächsten Lieferung, andernfalls wird er spätestens nach 30 Tagen ausgezahlt." },
          "notes": "Für das am 27.10.2026 (Tour 7) zurückgenommene Leergut.",
          "lines": [
            { "lineNumber": 1, "supplierSku": null, "gtin": null, "description": "Leergut Kasten 20 x 0,5 l Glas", "quantity": 14, "unit": "Kiste", "packSize": null,
              "unitPrice": 3.10, "discountPercent": null, "discountAmount": null, "lineNet": 43.40, "taxRatePercent": 19, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 2, "supplierSku": null, "gtin": null, "description": "Leergut Kasten 12 x 1,0 l Glas", "quantity": 6, "unit": "Kiste", "packSize": null,
              "unitPrice": 3.30, "discountPercent": null, "discountAmount": null, "lineNet": 19.80, "taxRatePercent": 19, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 3, "supplierSku": null, "gtin": null, "description": "Leergut Fass 30 l Edelstahl", "quantity": 1, "unit": "Stk", "packSize": null,
              "unitPrice": 30.00, "discountPercent": null, "discountAmount": null, "lineNet": 30.00, "taxRatePercent": 19, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 }
          ],
          "unreadableRegions": [],
          "x_custom": { "supplierCustomerRef": "104 220815" }
        }
        """;

    /// <summary>The credit note's number, which tells its requests apart from the invoice's.</summary>
    public const string CreditNoteNumber = "GS-2026-0918";

    /// <summary>Whether the request is about the demo credit note: its text layer, sent with the PDF, prints the number.</summary>
    public static bool IsCreditNote(LlmRequest request) =>
        request.Document?.PageTexts.Any(t => t.Contains(CreditNoteNumber, StringComparison.Ordinal)) == true;

    /// <summary>The delivery slip whose keywords cancel out ("Lieferung" is no title the rules know, "Die Rechnung folgt" negates the invoice title).</summary>
    public const string DeliveryNoteClassification = """
        { "documentType": "DeliveryNote", "confidence": 0.91, "language": "de", "supplierNameHint": "Bio-Hof Müller" }
        """;

    /// <summary>
    /// The Nordwind price list of the P2 demo (<see cref="Pdf.DemoPriceList"/>, docs/09 "Demo: manual mapping"): 14 pages are
    /// more than the heuristic splitter decides on its own (<c>MaxConfidentPageCount</c>), so the split stage asks the LLM,
    /// which keeps the list together.
    /// </summary>
    public const string PriceListSplit = """
        { "documents": [ { "startPage": 1, "endPage": 14, "documentType": "Other", "reason": "One price list: the same running header and column header on every page, pages numbered 1 to 14." } ] }
        """;

    /// <summary>
    /// The price list's classification: the classifier's types (docs/06) have no price list, so a good model answers
    /// <c>Other</c>; the document then has no form until a reviewer gives it a type (docs/11 §12.1).
    /// </summary>
    public const string PriceListClassification = """
        { "documentType": "Other", "confidence": 0.88, "language": "de", "supplierNameHint": "Nordwind Großhandel GmbH" }
        """;

    /// <summary>Whether the request is about the demo price list: its text (classification excerpt or split page texts) names the list.</summary>
    public static bool IsPriceList(LlmRequest request) =>
        request.Prompt.Task.Contains("Preisliste 2027", StringComparison.Ordinal);

    /// <summary>The recorded result for a request of the given stage, as a provider would have returned it.</summary>
    public static LlmResult For(LlmRequest request) => request.Stage switch
    {
        LlmStage.Split when IsPriceList(request) => new LlmResult
        {
            Json = PriceListSplit,
            Usage = new LlmUsage(InputTokens: 6120, OutputTokens: 61),
            Model = request.Model,
            StopReason = StopReasons.EndTurn,
            ProviderRequestId = "replay-demo-split-nordwind-preisliste-2027",
            Latency = TimeSpan.FromMilliseconds(1940),
        },
        LlmStage.Classify when IsPriceList(request) => new LlmResult
        {
            Json = PriceListClassification,
            Usage = new LlmUsage(InputTokens: 1710, OutputTokens: 41),
            Model = request.Model,
            StopReason = StopReasons.EndTurn,
            ProviderRequestId = "replay-demo-classify-nordwind-preisliste-2027",
            Latency = TimeSpan.FromMilliseconds(910),
        },
        LlmStage.Extract when request.OutputSchema?.Version.StartsWith("1+x.", StringComparison.Ordinal) == true
                              && IsCreditNote(request) => new LlmResult
        {
            Json = CreditNoteExtractionWithCustomField,
            Usage = new LlmUsage(InputTokens: 2410, OutputTokens: 1105, CacheReadTokens: 2950),
            Model = request.Model,
            StopReason = StopReasons.EndTurn,
            ProviderRequestId = "replay-demo-extract-gs-2026-0918-x-custom",
            Latency = TimeSpan.FromMilliseconds(6830),
        },
        LlmStage.Extract when request.OutputSchema?.Version.StartsWith("1+x.", StringComparison.Ordinal) == true => new LlmResult
        {
            Json = InvoiceExtractionWithCustomField,
            Usage = new LlmUsage(InputTokens: 3950, OutputTokens: 1730, CacheReadTokens: 2950),
            Model = request.Model,
            StopReason = StopReasons.EndTurn,
            ProviderRequestId = "replay-demo-extract-2026-131-x-custom",
            Latency = TimeSpan.FromMilliseconds(9610),
        },
        LlmStage.Extract => new LlmResult
        {
            Json = InvoiceExtraction,
            Usage = new LlmUsage(InputTokens: 3890, OutputTokens: 1712, CacheReadTokens: 2950),
            Model = request.Model,
            StopReason = StopReasons.EndTurn,
            ProviderRequestId = "replay-demo-extract-2026-131",
            Latency = TimeSpan.FromMilliseconds(9420),
        },
        LlmStage.Classify => new LlmResult
        {
            Json = DeliveryNoteClassification,
            Usage = new LlmUsage(InputTokens: 1630, OutputTokens: 38),
            Model = request.Model,
            StopReason = StopReasons.EndTurn,
            ProviderRequestId = "replay-demo-classify-l-2026-0457",
            Latency = TimeSpan.FromMilliseconds(850),
        },
        _ => throw new InvalidOperationException(
            $"The demo data sent an unexpected {request.Stage} request (prompt {request.Prompt.CacheKey}). Only the invoice extraction (with and without the demo custom field), the credit note extraction with its custom field, the delivery-note classification and the price list's split and classification have demo answers; adjust the demo PDFs or add an answer to DemoAnswers."),
    };
}
