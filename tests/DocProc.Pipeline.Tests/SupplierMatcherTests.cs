using DocProc.Domain.Common;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Stages;
using DocProc.Pipeline.Support;

namespace DocProc.Pipeline.Tests;

public class SupplierMatcherTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static Supplier Supplier(string name, string? vatId = null, string[]? ibans = null, string[]? domains = null, string[]? aliases = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        Name = name,
        NormalizedName = Identifiers.NormalizeName(name),
        VatId = vatId,
        Ibans = ibans?.ToList() ?? [],
        EmailDomains = domains?.ToList() ?? [],
        Aliases = aliases?.ToList() ?? [],
    };

    private static SupplierSignals Signals(string text, string? senderDomain = null) =>
        new(SupplierMatcher.FindVatIds(text), Identifiers.NormalizeCompact(text) ?? string.Empty, senderDomain, text);

    [Fact]
    public void Vat_id_in_the_text_wins_whatever_the_formatting()
    {
        var metro = Supplier("METRO Deutschland GmbH", vatId: "DE811177936");
        var other = Supplier("Musterfirma GmbH", vatId: "DE123456789", domains: ["example.com"]);

        var match = SupplierMatcher.Match([other, metro], Signals("Lieferant: Musterfirma GmbH\nUSt-IdNr. DE 811 177 936", senderDomain: "example.com"));

        match.ShouldNotBeNull();
        match.Supplier.ShouldBeSameAs(metro);
        match.Method.ShouldBe("vatId");
        match.Confidence.ShouldBe(SupplierMatcher.VatIdConfidence);
    }

    [Fact]
    public void Finds_eu_vat_ids_in_compact_form()
    {
        var ids = SupplierMatcher.FindVatIds("Rechnung von ATU12345678, UID HR12345678901 und DE 123 456 789; Kunde 10234");

        ids.ShouldBe(["ATU12345678", "HR12345678901", "DE123456789"], ignoreOrder: true);
    }

    [Fact]
    public void Iban_beats_the_sender_domain_and_the_name()
    {
        var bank = Supplier("Getränke Hoffmann GmbH", ibans: ["DE89 3704 0044 0532 0130 00"]);
        var named = Supplier("Musterfirma GmbH", domains: ["musterfirma.de"]);

        var match = SupplierMatcher.Match([named, bank], Signals("Musterfirma GmbH\nIBAN: DE89370400440532013000", senderDomain: "musterfirma.de"));

        match.ShouldNotBeNull().Supplier.ShouldBeSameAs(bank);
        match.Method.ShouldBe("iban");
    }

    [Fact]
    public void Sender_domain_matches_subdomains_too()
    {
        var supplier = Supplier("Konzum plus d.o.o.", domains: ["konzum.hr"]);

        SupplierMatcher.Match([supplier], Signals("nothing useful", senderDomain: "billing.konzum.hr")).ShouldNotBeNull().Method.ShouldBe("emailDomain");
        SupplierMatcher.Match([supplier], Signals("nothing useful", senderDomain: "notkonzum.hr")).ShouldBeNull();
        SupplierMatcher.DomainOf("Buchhaltung <rechnung@Fischhandel-Mueller.example>").ShouldBe("fischhandel-mueller.example");
        SupplierMatcher.DomainOf("no-address").ShouldBeNull();
    }

    [Fact]
    public void Fuzzy_name_needs_most_tokens_of_the_supplier_name_or_an_alias()
    {
        var mueller = Supplier("Bio-Hof Müller", aliases: ["Biohof Mueller KG"]);
        var other = Supplier("Pekara Dubravica d.o.o.");

        // Names are compared after Identifiers.NormalizeName: diacritics folded, "Bio-Hof" becomes one token.
        var byName = SupplierMatcher.Match([other, mueller], Signals("Lieferung vom Bio-Hof Muller, Danke"));
        byName.ShouldNotBeNull().Supplier.ShouldBeSameAs(mueller);
        byName.Method.ShouldBe("name");
        byName.Confidence.ShouldBe(0.8);

        SupplierMatcher.Match([mueller], Signals("Biohof Mueller KG")).ShouldNotBeNull();
        SupplierMatcher.Match([mueller], Signals("Hof Müller")).ShouldBeNull("one of two tokens is below the 0.8 overlap");
        SupplierMatcher.Match([mueller], SupplierSignals.Empty).ShouldBeNull();
    }

    [Theory]
    [InlineData("application/octet-stream", "scan.PDF", ItemKind.Pdf)]
    [InlineData("image/jpeg", "photo", ItemKind.Image)]
    [InlineData("application/octet-stream", "invoice.xml", ItemKind.Xml)]
    [InlineData("text/xml", "data", ItemKind.Xml)]
    [InlineData("application/x-zip-compressed", "batch.zip", ItemKind.Archive)]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "prices", ItemKind.Office)]
    [InlineData("text/plain", "notes.txt", ItemKind.Text)]
    [InlineData("application/octet-stream", "blob", ItemKind.Other)]
    public void Item_kind_comes_from_the_extension_first_and_the_content_type_second(string mime, string fileName, ItemKind expected) =>
        ItemKinds.Detect(mime, fileName).ShouldBe(expected);
}
