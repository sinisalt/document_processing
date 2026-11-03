using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Documents;
using DocProc.Application.Outputs;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Llm.Schema;
using DocProc.Outputs.Renderer;

namespace DocProc.Integration.Tests.Outputs;

/// <summary>
/// The values view output mappings render (docs/11 §5.6): the same view for a coded invoice (a version 1 tree) and
/// for a definition tree of the same data, the price list's rows with tier prices, the blocks besides <c>doc</c>
/// (<c>roles.lines</c>, <c>meta</c>, <c>supplier</c>, <c>entity</c>, <c>source</c>, <c>links</c>), the field envelopes
/// <c>$field</c> reads, and the article-price mapping of §13.10 rendered over a price list, 10,000 rows included.
/// </summary>
public sealed class ValuesViewTests(RendererFixture fixture) : IClassFixture<RendererFixture>
{
    private static readonly FieldSource Llm = new(SourceMethod.Llm, Page: 1);

    private static CompiledDefinition Compile(TypeDefinition definition)
    {
        var result = DefinitionSchemaCompiler.Compile(definition);
        result.Problems.ShouldBeEmpty();
        return result.Compiled!;
    }

    private static readonly DocumentDataContext Context = new(new DocumentDataMeta(Guid.Parse("6c1f0000-0000-0000-0000-000000000001"), 7, "CreditNote", 3, "Approved", new DateTimeOffset(2026, 10, 29, 9, 12, 0, TimeSpan.Zero), "j.weber")
    {
        TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Event = "document.approved",
        EventAt = new DateTimeOffset(2026, 10, 29, 9, 12, 1, TimeSpan.Zero),
    })
    {
        Supplier = new DocumentDataSupplier(Guid.Parse("9b1e0000-0000-0000-0000-000000000001"), "Nordwind Großhandel GmbH", "DE811111111")
        {
            Attributes = new JsonObject { ["erpVendorId"] = "V-10042", ["defaultCostCentre"] = "4711" },
        },
        Entity = new DocumentDataEntity(null, null, "Acme GmbH", "DE999999999"),
        Source = new DocumentDataSource("AP mailbox", "Mailbox", null, "rechnungsversand@nordwind.example", null, null, "GS-2026-0042.pdf", "Gutschrift GS-2026-0042", new DateTimeOffset(2026, 10, 29, 23, 30, 0, TimeSpan.Zero), new DateOnly(2026, 10, 30)),
        Links = new DocumentDataLinks("https://dms.example/api/v1/documents/6c1f/original", "https://dms.example/api/v1/documents/6c1f"),
    };

    private static Invoice CreditNote() => new()
    {
        Kind = FieldValue.Extracted(InvoiceKind.CreditNote, Llm, 0.99),
        InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("GS-2026-0042"), Llm, 0.98),
        IssueDate = FieldValue.Extracted(new DateOnly(2026, 10, 28), Llm, 0.97),
        Currency = FieldValue.Extracted("EUR", Llm, 0.99),
        OriginalInvoiceReference = FieldValue.Extracted(DocumentIdentifier.Create("RE-2026-9917"), Llm, 0.9),
        SubtotalNet = FieldValue.Extracted(new Money(-100.00m, "EUR"), Llm, 0.99),
        TotalTax = FieldValue.Extracted(new Money(-19.00m, "EUR"), Llm, 0.99),
        TotalGross = FieldValue.Extracted(new Money(-119.00m, "EUR"), Llm, 0.99),
        Lines =
        [
            new InvoiceLine
            {
                LineNumber = 1,
                SupplierSku = FieldValue.Extracted("4711", Llm, 0.95),
                Description = FieldValue.Extracted("Kaffee Crema 1kg", Llm, 0.95),
                Quantity = FieldValue.Extracted(-2m, Llm, 0.95),
                UnitPrice = FieldValue.Extracted(new Money(50.00m, "EUR"), Llm, 0.95),
                LineNet = FieldValue.Extracted(new Money(-100.00m, "EUR"), Llm, 0.95),
            },
        ],
    };

    [Fact]
    public void A_coded_credit_note_and_its_definition_tree_give_the_same_view()
    {
        var adopted = Compile(StandardTemplates.Find("std.credit-note")!);
        var fork = Compile(DefinitionStorageTests.WithCustomFields(StandardTemplates.Find("std.credit-note")!));
        var v1 = JsonSerializer.SerializeToNode(CreditNote(), CanonicalJson.Options)!.AsObject();
        var v2 = (JsonObject)v1.DeepClone();
        DefinitionTree.Complete(v2, fork.Descriptors).ShouldBeTrue("the fork adds its custom fields to the tree");

        var coded = DocumentDataView.Build(v1, adopted, Context);
        var tree = DocumentDataView.Build(v2, fork, Context);

        var treeDoc = tree["doc"]!.AsObject();
        treeDoc.Remove("supplierCustomerRef").ShouldBeTrue();
        treeDoc.Remove("ext").ShouldBeTrue();
        // The fork's row bag shows in the rows and in roles.lineItems, which are the rows.
        foreach (var line in treeDoc["lines"]!.AsArray().Concat(tree["roles"]!["lineItems"]!.AsArray()))
        {
            line!.AsObject().Remove("ext").ShouldBeTrue();
        }

        JsonNode.DeepEquals(coded["doc"], treeDoc).ShouldBeTrue($"{coded["doc"]!.ToJsonString()}\n{treeDoc.ToJsonString()}");
        foreach (var block in new[] { "roles", "meta", "supplier", "entity", "source", "links" })
        {
            JsonNode.DeepEquals(coded[block], tree[block]).ShouldBeTrue(block);
        }

        // The blocks of docs/11 §5.6, as an output mapping reads them.
        var roles = coded["roles"]!;
        roles["documentNumber"]!.GetValue<string>().ShouldBe("GS-2026-0042");
        roles["documentDate"]!.GetValue<string>().ShouldBe("2026-10-28");
        roles["total"]!["amount"]!.GetValue<decimal>().ShouldBe(-119.00m);
        roles["lines"]!.ToJsonString().ShouldBe("""[{"net":{"amount":-100.00,"currency":"EUR"},"quantity":-2,"unitPrice":{"amount":50.00,"currency":"EUR"},"sku":"4711"}]""");
        coded["doc"]!["originalInvoiceReference"]!.GetValue<string>().ShouldBe("RE-2026-9917");
        var meta = coded["meta"]!;
        meta["typeKey"]!.GetValue<string>().ShouldBe("CreditNote");
        meta["tenantId"]!.GetValue<string>().ShouldBe("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        meta["event"]!.GetValue<string>().ShouldBe("document.approved");
        meta["eventAt"]!.GetValue<string>().ShouldBe("2026-10-29T09:12:01Z");
        meta["approvedAt"]!.GetValue<string>().ShouldBe("2026-10-29T09:12:00Z");
        meta["supersedes"].ShouldBeNull();
        coded["supplier"]!["attributes"]!["erpVendorId"]!.GetValue<string>().ShouldBe("V-10042");
        coded["entity"]!["name"]!.GetValue<string>().ShouldBe("Acme GmbH");
        coded["source"]!["receivedAt"]!.GetValue<string>().ShouldBe("2026-10-29T23:30:00Z");
        coded["source"]!["receivedDate"]!.GetValue<string>().ShouldBe("2026-10-30", "the day in the tenant's time zone");
        coded["links"]!["original"]!.GetValue<string>().ShouldEndWith("/original");
    }

    [Fact]
    public void A_document_without_a_definition_has_an_empty_doc_and_the_other_blocks()
    {
        var view = DocumentDataView.BuildWithoutDefinition(Context with { Supplier = null });

        view["doc"]!.AsObject().Count.ShouldBe(0);
        view["roles"]!.AsObject().Count.ShouldBe(0);
        view["supplier"].ShouldBeNull();
        view["meta"]!["documentId"]!.GetValue<string>().ShouldBe("6c1f0000-0000-0000-0000-000000000001");
    }

    [Fact]
    public void The_field_envelopes_give_status_confidence_method_and_the_normalized_value()
    {
        var compiled = Compile(StandardTemplates.Find("std.credit-note")!);
        var tree = JsonSerializer.SerializeToNode(CreditNote(), CanonicalJson.Options)!.AsObject();

        var fields = DocumentDataView.FieldEnvelopes(tree, compiled.Descriptors);

        fields["invoiceNumber"]!["status"]!.GetValue<string>().ShouldBe("Extracted");
        fields["invoiceNumber"]!["confidence"]!.GetValue<double>().ShouldBe(0.98);
        fields["invoiceNumber"]!["method"]!.GetValue<string>().ShouldBe("Llm");
        fields["invoiceNumber"]!["normalized"]!.GetValue<string>().ShouldBe(DocumentIdentifier.Create("GS-2026-0042").Normalized);
        fields["lines[0].quantity"]!["normalized"]!.GetValue<decimal>().ShouldBe(-2m);
        fields["dueDate"]!["status"]!.GetValue<string>().ShouldBe("Missing");
        DocumentDataView.FieldEnvelopes(tree, compiled.Descriptors, max: 3).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Field_in_a_mapping_reads_the_envelopes()
    {
        var compiled = Compile(StandardTemplates.Find("std.credit-note")!);
        var tree = JsonSerializer.SerializeToNode(CreditNote(), CanonicalJson.Options)!.AsObject();

        var result = await fixture.Renderer.RenderAsync(
            new RenderRequest("""{ "lowConfidence": $field("lines[0].quantity").confidence < 0.96, "numberMethod": $field("invoiceNumber").method }""", DocumentDataView.Build(tree, compiled, Context), new DateOnly(2026, 11, 3))
            {
                Fields = DocumentDataView.FieldEnvelopes(tree, compiled.Descriptors),
            },
            TestContext.Current.CancellationToken);

        result.Output!.ToJsonString().ShouldBe("""{"lowConfidence":true,"numberMethod":"Llm"}""");
    }

    /// <summary>The <c>article-price-import</c> v1 mapping of docs/11 §13.10, as written there.</summary>
    internal const string ArticlePriceImport = """
        (
          $root := $;
          {
            "header": {
              "vendorNo": $required(supplier.attributes.erpVendorId, "Supplier has no ERP vendor id"),
              "priceListId": roles.documentNumber ? roles.documentNumber : supplier.attributes.erpVendorId & "-" & roles.validFrom,
              "validFrom": $fmtDate(roles.validFrom, "yyyyMMdd"),
              "validTo": roles.validUntil ? $fmtDate(roles.validUntil, "yyyyMMdd") : "99991231",
              "currency": roles.currency,
              "priceType": doc.priceBasis = "Gross" ? "B" : "N",
              "mode": doc.listScope = "Changes" ? "UPSERT" : "REPLACE",
              "dmsId": meta.documentId,
              "revision": meta.version
            },
            "prices": [ doc.lines[$not(status = "Discontinued")].{
              "vendorArticleNo": supplierSku,
              "ean": gtin,
              "text": $substring(description, 0, 40),
              "group": category,
              "orderUnit": $table("erp-uom", unit, "ST"),
              "contentQty": packSize,
              "contentUnit": $table("erp-uom", packUnit, "ST"),
              "priceUnit": priceQuantity ? priceQuantity : 1,
              "price": price.amount,
              "validFrom": $fmtDate(lineValidFrom ? lineValidFrom : $root.roles.validFrom, "yyyyMMdd"),
              "scales": [ tiers.{ "fromQty": minQuantity, "price": price.amount } ]
            } ],
            "delist": [ doc.lines[status = "Discontinued"].supplierSku ]
          }
        )
        """;

    private static readonly LookupTableSnapshot ErpUom = new("erp-uom", false, [new("KAR", "KAR"), new("ST", "ST"), new("PCK", "PCK")]);

    private static JsonObject Envelope(JsonNode? value) =>
        JsonSerializer.SerializeToNode(FieldValue.Extracted<JsonNode>(value!, confidence: 0.95), CanonicalJson.Options)!.AsObject();

    private static JsonObject Identifier(string text) => Envelope(new JsonObject { ["verbatim"] = text, ["normalized"] = text });

    private static JsonObject Eur(decimal amount) => Envelope(new JsonObject { ["amount"] = amount, ["currency"] = "EUR" });

    private static (JsonObject Tree, CompiledDefinition Compiled) PriceList(int rows, int descriptionLength = 0)
    {
        var compiled = Compile(StandardTemplates.Find("std.price-list")!);
        var tree = DefinitionTree.CreateEmpty(compiled.Descriptors);
        tree["priceListNumber"] = Identifier("PL-2027-01");
        tree["validFrom"] = Envelope(JsonValue.Create("2027-01-01"));
        tree["currency"] = Envelope(JsonValue.Create("EUR"));
        tree["priceBasis"] = Envelope(JsonValue.Create("Net"));
        tree["listScope"] = Envelope(JsonValue.Create("Full"));
        var lines = new JsonArray();
        JsonObject Row(string sku, string? gtin, string description, string category, string unit, decimal pack, string packUnit, decimal? priceQuantity, decimal price, string? validFrom, string? status, params (decimal From, decimal Price)[] tiers)
        {
            var row = DefinitionTree.EmptyRow(compiled.Descriptors, "lines");
            row["supplierSku"] = Identifier(sku);
            if (gtin is not null)
            {
                row["gtin"] = Identifier(gtin);
            }

            row["description"] = Envelope(JsonValue.Create(description));
            row["category"] = Envelope(JsonValue.Create(category));
            row["unit"] = Envelope(JsonValue.Create(unit));
            row["packSize"] = Envelope(JsonValue.Create(pack));
            row["packUnit"] = Envelope(JsonValue.Create(packUnit));
            if (priceQuantity is { } q)
            {
                row["priceQuantity"] = Envelope(JsonValue.Create(q));
            }

            row["price"] = Eur(price);
            if (validFrom is not null)
            {
                row["lineValidFrom"] = Envelope(JsonValue.Create(validFrom));
            }

            if (status is not null)
            {
                row["status"] = Envelope(JsonValue.Create(status));
            }

            row["tiers"] = new JsonArray([.. tiers.Select(t => (JsonNode)new JsonObject { ["minQuantity"] = Envelope(JsonValue.Create(t.From)), ["price"] = Eur(t.Price) })]);
            return row;
        }

        if (rows <= 5)
        {
            lines.Add(Row("4711", "4006381333931", "Kaffee Crema 1kg", "Kaffee", "KAR", 6, "ST", null, 50.00m, null, null, (10, 48.50m), (50, 46.00m)));
            lines.Add(Row("4712", "4006381333948", "Espresso Bohne 1kg", "Kaffee", "KAR", 6, "ST", null, 56.40m, null, "Unchanged"));
            lines.Add(Row("5120", null, "Filtertüten Gr. 4", "Zubehör", "KAR", 40, "PCK", 100, 31.90m, "2027-02-01", null));
            lines.Add(Row("3990", null, "Kakao alt", "Kakao", "ST", 1, "ST", null, 3.10m, null, "Discontinued"));
            lines.Add(Row("3991", null, "Kakao alt 2", "Kakao", "ST", 1, "ST", null, 3.20m, null, "Discontinued"));
        }
        else
        {
            var description = new string('D', descriptionLength);
            for (var i = 0; i < rows; i++)
            {
                lines.Add(Row($"SKU-{i:D10}", "4006381333931", description, new string('C', 60), "KAR", 6, "ST", 1, 50.00m, "2027-01-01", "Changed", (10, 48.50m), (50, 46.00m), (100, 44.00m)));
            }
        }

        tree["lines"] = lines;
        return (tree, compiled);
    }

    private static DocumentDataContext PriceListContext() => Context with
    {
        Meta = new DocumentDataMeta(Guid.Parse("8d2a0000-0000-0000-0000-000000000001"), 3, "price-list", 1, "Approved"),
    };

    [Fact]
    public void A_price_lists_view_has_its_rows_with_tier_prices_and_the_list_roles()
    {
        var (tree, compiled) = PriceList(5);

        var view = DocumentDataView.Build(tree, compiled, PriceListContext());

        var first = view["doc"]!["lines"]![0]!;
        first["supplierSku"]!.GetValue<string>().ShouldBe("4711");
        first["price"]!.ToJsonString().ShouldBe("""{"amount":50.00,"currency":"EUR"}""");
        first["tiers"]!.ToJsonString().ShouldBe("""[{"minQuantity":10,"price":{"amount":48.50,"currency":"EUR"}},{"minQuantity":50,"price":{"amount":46.00,"currency":"EUR"}}]""");
        view["doc"]!["lines"]![1]!["tiers"]!.AsArray().Count.ShouldBe(0);
        view["doc"]!["lines"]![2]!["gtin"].ShouldBeNull("Missing is null");
        view["roles"]!["documentNumber"]!.GetValue<string>().ShouldBe("PL-2027-01");
        view["roles"]!["validFrom"]!.GetValue<string>().ShouldBe("2027-01-01");
        view["roles"]!["validUntil"].ShouldBeNull();
        view["roles"]!["lines"]![0]!.ToJsonString().ShouldBe("""{"unitPrice":{"amount":50.00,"currency":"EUR"},"sku":"4711"}""");
    }

    [Fact]
    public async Task The_article_price_mapping_of_the_design_renders_a_price_list()
    {
        var (tree, compiled) = PriceList(5);
        var view = DocumentDataView.Build(tree, compiled, PriceListContext());

        var result = await fixture.Renderer.RenderAsync(new RenderRequest(ArticlePriceImport, view, new DateOnly(2026, 11, 3)) { Lookups = [ErpUom] }, TestContext.Current.CancellationToken);

        result.Error.ShouldBeNull();
        // As docs/11 §13.10 shows it; numbers keep the text they were read with (50.00), a missing gtin is null here
        // because the values view writes Missing as null (the design's example drops the key; see the correction there).
        result.OutputText.ShouldBe(JsonNode.Parse("""
            {"header":{"vendorNo":"V-10042","priceListId":"PL-2027-01","validFrom":"20270101","validTo":"99991231","currency":"EUR","priceType":"N","mode":"REPLACE","dmsId":"8d2a0000-0000-0000-0000-000000000001","revision":3},
             "prices":[{"vendorArticleNo":"4711","ean":"4006381333931","text":"Kaffee Crema 1kg","group":"Kaffee","orderUnit":"KAR","contentQty":6,"contentUnit":"ST","priceUnit":1,"price":50.00,"validFrom":"20270101","scales":[{"fromQty":10,"price":48.50},{"fromQty":50,"price":46.00}]},
              {"vendorArticleNo":"4712","ean":"4006381333948","text":"Espresso Bohne 1kg","group":"Kaffee","orderUnit":"KAR","contentQty":6,"contentUnit":"ST","priceUnit":1,"price":56.40,"validFrom":"20270101","scales":[]},
              {"vendorArticleNo":"5120","ean":null,"text":"Filtertüten Gr. 4","group":"Zubehör","orderUnit":"KAR","contentQty":40,"contentUnit":"PCK","priceUnit":100,"price":31.90,"validFrom":"20270201","scales":[]}],
             "delist":["3990","3991"]}
            """)!.ToJsonString());

        // Without the vendor number the render stops with the mapping's message.
        var noVendor = DocumentDataView.Build(tree, compiled, PriceListContext() with { Supplier = Context.Supplier! with { Attributes = new JsonObject() } });
        var missing = await fixture.Renderer.RenderAsync(new RenderRequest(ArticlePriceImport, noVendor, new DateOnly(2026, 11, 3)) { Lookups = [ErpUom] }, TestContext.Current.CancellationToken);
        missing.Error!.Kind.ShouldBe(RenderErrorKinds.Required);
        missing.Error.Message.ShouldBe("Supplier has no ERP vendor id");
    }

    [Fact]
    public async Task A_price_list_of_ten_thousand_rows_of_the_longest_shape_renders_under_five_megabytes_with_a_schema_error_naming_the_row()
    {
        // docs/11 §13.10: the type's maxItems keeps every list that passes validation under the 5 MB cap.
        var (tree, compiled) = PriceList(10_000, descriptionLength: 400);
        var view = DocumentDataView.Build(tree, compiled, PriceListContext());

        var schema = JsonNode.Parse("""{"type":"object","properties":{"prices":{"type":"array","items":{"type":"object","properties":{"price":{"type":"number","maximum":49}}}}}}""")!.AsObject();
        var result = await fixture.Renderer.RenderAsync(new RenderRequest(ArticlePriceImport, view, new DateOnly(2026, 11, 3)) { Lookups = [ErpUom], TargetSchema = schema }, TestContext.Current.CancellationToken);

        result.Error.ShouldBeNull();
        result.OutputBytes.ShouldBeLessThan(RenderLimits.MaxOutputBytes);
        result.Output!["prices"]!.AsArray().Count.ShouldBe(10_000);
        result.SchemaErrors.Count.ShouldBe(TargetSchemaCheck.MaxErrors, "every row is over the maximum; the report is capped");
        result.SchemaErrors[0].Path.ShouldBe("prices[0].price");
        result.SchemaErrors[0].Keyword.ShouldBe("maximum");
        // No time-limit error: the render and the check fit the 2 s of processor time, which start once the view is read
        // (about 0.95 s on the 4-core development machine, 2.6 s in all with sending and reading the 20 MB view).
        result.RenderTime.ShouldBeGreaterThan(TimeSpan.Zero);
    }
}
