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

    /// <summary>The delivery slip whose keywords cancel out ("Lieferung" is no title the rules know, "Die Rechnung folgt" negates the invoice title).</summary>
    public const string DeliveryNoteClassification = """
        { "documentType": "DeliveryNote", "confidence": 0.91, "language": "de", "supplierNameHint": "Bio-Hof Müller" }
        """;

    /// <summary>The recorded result for a request of the given stage, as a provider would have returned it.</summary>
    public static LlmResult For(LlmRequest request) => request.Stage switch
    {
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
            $"The demo data sent an unexpected {request.Stage} request (prompt {request.Prompt.CacheKey}). Only the invoice extraction and the delivery-note classification have demo answers; adjust the demo PDFs or add an answer to DemoAnswers."),
    };
}
