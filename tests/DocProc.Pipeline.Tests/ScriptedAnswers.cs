namespace DocProc.Pipeline.Tests;

/// <summary>Canned structured-output answers for the scripted provider, shaped like the LLM contracts of DocProc.Llm.</summary>
internal static class ScriptedAnswers
{
    public const string ClassifyInvoice = """{ "documentType": "Invoice", "confidence": 0.9, "language": "de", "supplierNameHint": "Musterfirma GmbH" }""";

    /// <summary>The two-page German invoice of <see cref="Pdf.TestPdfs.GermanInvoice"/>, addressed to tenant A.</summary>
    public const string ExtractGermanInvoice = """
        {
          "kind": "Invoice", "invoiceNumber": "RE-2026-0042", "issueDate": "2026-03-15", "deliveryDate": null,
          "servicePeriodFrom": null, "servicePeriodTo": null, "dueDate": "2026-03-29", "currency": "EUR",
          "supplier": { "name": "Musterfirma GmbH", "vatId": null, "taxId": null, "registrationNumber": null,
                        "addressLine1": "Industriestraße 12", "addressLine2": null, "postalCode": "80339", "city": "München", "countryCode": "DE",
                        "email": null, "phone": null, "iban": null, "bic": null },
          "customer": { "name": "Gasthaus zur Post GmbH", "vatId": "DE123456789", "taxId": null, "registrationNumber": null,
                        "addressLine1": null, "addressLine2": null, "postalCode": null, "city": null, "countryCode": "DE",
                        "email": null, "phone": null, "iban": null, "bic": null },
          "orderReferences": [], "deliveryNoteReferences": ["LS-2026-0917"], "originalInvoiceReference": null,
          "subtotalNet": 1250.00,
          "taxBreakdown": [ { "ratePercent": 19, "taxableAmount": 1250.00, "taxAmount": 237.50, "categoryCode": "S" } ],
          "totalTax": 237.50, "totalGross": 1487.50, "amountDue": 1487.50, "rounding": null,
          "chargesAndAllowances": [],
          "paymentTerms": { "dueDate": "2026-03-29", "discountPercent": null, "discountUntil": null, "paymentReference": null, "iban": null, "bic": null, "rawText": "Zahlbar innerhalb von 14 Tagen ohne Abzug." },
          "notes": null,
          "lines": [
            { "lineNumber": 1, "supplierSku": null, "gtin": null, "description": "Schraube M8", "quantity": 100, "unit": "Stk", "packSize": null, "unitPrice": 0.50,
              "discountPercent": null, "discountAmount": null, "lineNet": 50.00, "taxRatePercent": 19, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 2, "supplierSku": null, "gtin": null, "description": "Mutter M8", "quantity": 100, "unit": "Stk", "packSize": null, "unitPrice": 0.20,
              "discountPercent": null, "discountAmount": null, "lineNet": 20.00, "taxRatePercent": 19, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 },
            { "lineNumber": 3, "supplierSku": null, "gtin": null, "description": "Montageleistung", "quantity": 10, "unit": "Std", "packSize": null, "unitPrice": 118.00,
              "discountPercent": null, "discountAmount": null, "lineNet": 1180.00, "taxRatePercent": 19, "lineGross": null, "depositAmount": null, "lotNumber": null, "bestBefore": null, "page": 1 }
          ],
          "unreadableRegions": []
        }
        """;
}
