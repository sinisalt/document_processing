using DocProc.Application.Outputs;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Configuration;
using DocProc.Infrastructure.Outputs;
using Microsoft.Extensions.Options;

namespace DocProc.Api.Tests;

/// <summary>
/// The values view builder over the database (docs/11 §5.6): a stored document's view through its adopted type, with
/// the supplier's attributes, the tenant as receiving entity, the ingestion's source facts, the links under the public
/// base URL, the event being rendered, and the field envelopes on request; a document without a type has an empty doc.
/// </summary>
public sealed class DocumentDataViewBuilderTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DocumentDataViewBuilder Builder(Infrastructure.Persistence.DocProcDbContext db) =>
        new(db, new CatalogueConfigResolver(db, new EfTenantFeatures(db), new PublishedDefinitionCache()), factory.Clock, Options.Create(new OutputOptions { PublicBaseUrl = "https://dms.example/" }));

    [Fact]
    public async Task Builds_the_view_of_a_stored_invoice_with_supplier_entity_source_and_links()
    {
        var tenant = factory.CreateTenant("View builder");
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.Approved, SupplierName = "Nordwind Großhandel GmbH", FileName = "RE-2026-0042.pdf" });
        factory.Run(db =>
        {
            db.Suppliers.Single(s => s.TenantId == tenant).AttributesJson = """{"erpVendorId":"V-10042"}""";
            db.Ingestions.Single(i => i.Id == seeded.IngestionId).SourceSender = "rechnungsversand@nordwind.example";
        });
        await using var db = factory.PlatformDb();

        var built = await Builder(db).BuildAsync(new DataViewRequest(seeded.DocumentId) { Event = "document.approved", EventAt = factory.Clock.GetUtcNow(), IncludeFields = true }, Ct);

        built.ShouldNotBeNull();
        built.TenantId.ShouldBe(tenant);
        built.TypeKey.ShouldBe("Invoice");
        built.Today.ShouldBe(new DateOnly(2026, 10, 30), "today in the tenant's time zone");
        var view = built.View;
        view["doc"]!["invoiceNumber"]!.GetValue<string>().ShouldBe("RE-2026-0042");
        view["roles"]!["total"]!["amount"]!.GetValue<decimal>().ShouldBe(166.50m);
        view["roles"]!["lines"]!.AsArray().Count.ShouldBe(2);
        view["meta"]!["typeVersion"]!.GetValue<int>().ShouldBe(1);
        view["meta"]!["event"]!.GetValue<string>().ShouldBe("document.approved");
        view["meta"]!["tenantId"]!.GetValue<string>().ShouldBe(tenant.ToString());
        view["meta"]!["approvedBy"]!.GetValue<string>().ShouldBe("seed");
        view["supplier"]!["name"]!.GetValue<string>().ShouldBe("Nordwind Großhandel GmbH");
        view["supplier"]!["attributes"]!["erpVendorId"]!.GetValue<string>().ShouldBe("V-10042");
        view["entity"]!["name"]!.GetValue<string>().ShouldBe(factory.Query(d => d.Tenants.Single(t => t.Id == tenant).LegalName));
        view["entity"]!["code"].ShouldBeNull("receiving entities come with P4");
        view["source"]!["sender"]!.GetValue<string>().ShouldBe("rechnungsversand@nordwind.example");
        view["source"]!["fileName"]!.GetValue<string>().ShouldBe("RE-2026-0042.pdf");
        view["source"]!["connector"]!.GetValue<string>().ShouldBe("ApiUpload");
        view["source"]!["receivedDate"]!.GetValue<string>().ShouldBe("2026-10-30");
        view["links"]!["original"]!.GetValue<string>().ShouldBe($"https://dms.example/api/v1/documents/{seeded.DocumentId}/original");
        built.Fields!["invoiceNumber"]!["method"]!.GetValue<string>().ShouldBe("Llm");
        built.Fields["lines[1].quantity"]!["confidence"]!.GetValue<double>().ShouldBe(0.72);
    }

    [Fact]
    public async Task A_document_without_a_type_has_an_empty_doc_and_an_unknown_document_is_none()
    {
        var tenant = factory.CreateTenant("View builder unknown");
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Type = DocumentTypeKey.Unknown, Invoice = null, SupplierName = null });
        await using var db = factory.PlatformDb();

        var built = (await Builder(db).BuildAsync(new DataViewRequest(seeded.DocumentId), Ct))!;

        built.View["doc"]!.AsObject().Count.ShouldBe(0);
        built.View["supplier"].ShouldBeNull();
        built.View["meta"]!["event"].ShouldBeNull();
        built.Fields.ShouldBeNull();
        (await Builder(db).BuildAsync(new DataViewRequest(Guid.NewGuid()), Ct)).ShouldBeNull();
    }
}
