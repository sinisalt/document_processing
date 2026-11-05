using System.Globalization;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Tests.Pdf;
using P = DocProc.Pipeline.Tests.Pdf.DemoNordwindInvoices;

namespace DocProc.Pipeline.Tests.Layouts;

/// <summary>
/// Nordwind's two invoice layouts as a reviewer would save them (docs/11 §8.1, §8.5) for the demo invoices of
/// <see cref="DemoNordwindInvoices"/>: <c>nordwind-invoice-erp</c> ("Rechnung" in the title and the VAT id on page 1) and
/// <c>nordwind-invoice-webshop</c> ("RECHNUNG / INVOICE" in the header and the VAT id on the last page), each with a
/// field mapping of labels, regions, constants and a <c>column-table</c> for the lines. Positions are in the normalised
/// 1000 × 1000 page space, computed from the generator's points.
/// </summary>
internal static class NordwindLayouts
{
    public const string ErpKey = "nordwind-invoice-erp";
    public const string WebShopKey = "nordwind-invoice-webshop";

    public const string ErpRule = """
        { "all": [ { "text": "Rechnung", "page": 1, "region": "title" }, { "supplierValue": "vatId", "page": 1 } ],
          "none": [ { "text": "Gutschrift", "page": 1 } ] }
        """;

    public const string WebShopRule = """
        { "all": [ { "text": "RECHNUNG / INVOICE", "page": 1, "region": "header" }, { "supplierValue": "vatId", "page": "last" } ] }
        """;

    private static string U(double points) => Math.Round(points * 1000 / P.PageWidth).ToString(CultureInfo.InvariantCulture);

    private static string Column(double x0, double x1, string field, string header) =>
        $$"""{ "x0": {{U(x0)}}, "x1": {{U(x1)}}, "field": "{{field}}", "header": "{{header}}" }""";

    private const string Totals = """
        "currency": { "rules": [ { "strategy": "regex-anywhere", "pattern": "\\d,\\d{2}\\s+(EUR)\\b", "group": 1 } ] },
        "supplier.name": { "rules": [ { "strategy": "constant", "value": "Nordwind Großhandel GmbH" } ] },
        "taxBreakdown.rate": { "rules": [ { "strategy": "constant", "value": "19" } ] },
        """;

    public static string ErpMapping(string numberLabel = "Rechnungs-Nr.") => $$"""
        { "locale": "de-DE",
          "fields": {
            "invoiceNumber": { "rules": [ { "strategy": "label-right", "labels": ["{{numberLabel}}"], "page": 1 } ] },
            "issueDate": { "rules": [ { "strategy": "label-right", "labels": ["Rechnungsdatum"], "page": 1 } ] },
            {{Totals}}
            "supplier.vatId": { "rules": [ { "strategy": "regex-anywhere", "pattern": "USt-IdNr\\.\\s*([A-Z]{2}[0-9 ]{9,13})", "group": 1, "page": 1 } ], "transforms": [ { "op": "removeWhitespace" } ] },
            "customer.name": { "rules": [ { "strategy": "region", "page": 1, "bBox": [62, 118, 420, 140], "tolerance": 4 } ] },
            "subtotalNet": { "rules": [ { "strategy": "label-right", "labels": ["Summe netto"] } ] },
            "totalTax": { "rules": [ { "strategy": "label-right", "labels": ["USt 19 %"] } ] },
            "totalGross": { "rules": [ { "strategy": "label-right", "labels": ["Gesamtbetrag"] } ] },
            "taxBreakdown.taxableAmount": { "rules": [ { "strategy": "label-right", "labels": ["Summe netto"] } ] },
            "taxBreakdown.taxAmount": { "rules": [ { "strategy": "label-right", "labels": ["USt 19 %"] } ] },
            "lines": { "strategy": "column-table", "page": "*", "bBox": [60, 265, 925, 940], "headerRow": { "y0": 265, "y1": 280 },
              "columns": [
                {{Column(36, 62, "lineNumber", "Pos")}},
                {{Column(62, 122, "supplierSku", "Art.-Nr.")}},
                {{Column(122, 324, "description", "Bezeichnung")}},
                {{Column(324, 366, "quantity", "Menge")}},
                {{Column(366, 411, "unit", "Einheit")}},
                {{Column(411, 476, "unitPrice", "Einzelpreis")}},
                {{Column(476, 551, "lineNet", "Betrag")}} ],
              "rowAnchor": "supplierSku", "stopAt": ["^Summe netto"] } },
          "requiredFields": ["invoiceNumber", "totalGross"] }
        """;

    public static string WebShopMapping() => $$"""
        { "locale": "de-DE",
          "fields": {
            "invoiceNumber": { "rules": [ { "strategy": "label-right", "labels": ["Rechnungsnummer / Invoice no.:"], "page": 1, "pattern": "WS-\\d{4}-\\d{6}" } ] },
            "issueDate": { "rules": [ { "strategy": "label-right", "labels": ["Datum / Date:"], "page": 1 } ] },
            {{Totals}}
            "supplier.vatId": { "rules": [ { "strategy": "regex-anywhere", "pattern": "USt-IdNr\\.\\s*(DE[0-9 ]{9,13})", "group": 1, "page": "last" } ], "transforms": [ { "op": "removeWhitespace" } ] },
            "customer.name": { "rules": [ { "strategy": "region", "page": 1, "bBox": [62, 125, 320, 142], "tolerance": 3 } ] },
            "subtotalNet": { "rules": [ { "strategy": "label-right", "labels": ["Zwischensumme / Subtotal"] } ] },
            "totalTax": { "rules": [ { "strategy": "label-right", "labels": ["MwSt. 19 % / VAT"] } ] },
            "totalGross": { "rules": [ { "strategy": "label-right", "labels": ["Gesamt / Total"] } ] },
            "taxBreakdown.taxableAmount": { "rules": [ { "strategy": "label-right", "labels": ["Zwischensumme / Subtotal"] } ] },
            "taxBreakdown.taxAmount": { "rules": [ { "strategy": "label-right", "labels": ["MwSt. 19 % / VAT"] } ] },
            "lines": { "strategy": "column-table", "page": "*", "bBox": [60, 228, 930, 940], "headerRow": { "y0": 228, "y1": 242 },
              "columns": [
                {{Column(36, 116, "supplierSku", "Artikel-Nr.")}},
                {{Column(116, 333, "description", "Beschreibung")}},
                {{Column(333, 385, "quantity", "Anzahl")}},
                {{Column(385, 434, "unit", "Einheit")}},
                {{Column(434, 476, "unitPrice", "Preis")}},
                {{Column(476, 553, "lineNet", "Summe")}} ],
              "rowAnchor": "supplierSku", "stopAt": ["^Zwischensumme", "^Fortsetzung"] } },
          "requiredFields": ["invoiceNumber", "totalGross"] }
        """;

    /// <summary>Adds a layout with one version in <paramref name="status"/> (Active by default, version 1).</summary>
    public static Task<(SupplierLayout Layout, SupplierLayoutVersion Version)> AddAsync(
        PipelineTestHost host,
        Guid supplierId,
        string key,
        string rule,
        string mapping,
        SupplierLayoutVersionStatus status = SupplierLayoutVersionStatus.Active,
        int warmup = 0,
        int priority = SupplierLayout.DefaultPriority,
        string type = "Invoice",
        SupplierLayoutScope scope = SupplierLayoutScope.Supplier,
        Guid? groupId = null) => host.WithDbAsync(async db =>
    {
        var now = host.Clock.GetUtcNow();
        var parsed = IdentificationRule.Parse(rule);
        var layout = new SupplierLayout
        {
            Id = Guid.CreateVersion7(),
            TenantId = PipelineTestHost.TenantA,
            Scope = scope,
            SupplierId = scope == SupplierLayoutScope.Supplier ? supplierId : null,
            GroupId = scope == SupplierLayoutScope.Group ? groupId : null,
            OwnerId = scope == SupplierLayoutScope.Supplier ? supplierId : groupId!.Value,
            DocumentTypeKey = type,
            LayoutKey = key,
            Name = key,
            Priority = priority,
            ActiveVersion = status is SupplierLayoutVersionStatus.Active or SupplierLayoutVersionStatus.Degraded ? 1 : null,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var version = new SupplierLayoutVersion
        {
            Id = Guid.CreateVersion7(),
            TenantId = PipelineTestHost.TenantA,
            LayoutId = layout.Id,
            Version = 1,
            Status = status,
            Origin = SupplierLayoutOrigin.Human,
            IdentificationJson = parsed.ToJson(),
            IdentificationHash = parsed.Hash,
            MappingJson = mapping,
            ReviewWarmup = warmup,
            CreatedBy = "test",
            CreatedAt = now,
        };
        db.SupplierLayouts.Add(layout);
        db.SupplierLayoutVersions.Add(version);
        await db.SaveChangesAsync();
        return (layout, version);
    });
}
