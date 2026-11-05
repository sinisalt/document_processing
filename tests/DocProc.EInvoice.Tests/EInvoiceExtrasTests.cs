using DocProc.Extraction.EInvoice;

namespace DocProc.EInvoice.Tests;

/// <summary>The e-invoice extras of docs/11 §9.3 and the restricted <c>xml-path</c> walker the layouts' rules use (§8.1).</summary>
public sealed class EInvoiceExtrasTests
{
    private static readonly EInvoiceParser Parser = new();
    private static readonly EInvoiceExtrasReader Reader = new();

    private static EInvoiceExtras Extras(string fixture)
    {
        var xml = Fixtures.Bytes(fixture);
        return Reader.Read(xml, Parser.Detect(xml).ShouldNotBeNull());
    }

    [Fact]
    public void A_UBL_invoice_gives_its_buyer_reference_and_line_properties_with_their_paths()
    {
        var extras = Extras(Fixtures.PeppolInvoice);

        extras.BuyerReference.ShouldNotBeNull().Value.ShouldBe("KUECHE-ROSENHEIM");
        extras.BuyerReference.Path.ShouldBe("Invoice/BuyerReference[1]");
        var batch = extras.LineProperties.First(p => p.Name == "Chargennummer");
        batch.Value.ShouldBe("L-2026-2991");
        extras.LineProperties.ShouldContain(p => p.Name == "Mindesthaltbarkeitsdatum" && p.Value == "2026-11-02");

        // The recorded path reads the value back through the walker.
        var root = Reader.Load(Fixtures.Bytes(Fixtures.PeppolInvoice));
        XmlPath.TryParse(batch.Path, out var error).ShouldNotBeNull(error).First(root).ShouldNotBeNull().Value.ShouldBe("L-2026-2991");
    }

    [Fact]
    public void A_CII_invoice_gives_the_same_extras_from_its_own_elements()
    {
        var extras = Extras(Fixtures.FacturXEn16931);

        extras.BuyerReference.ShouldNotBeNull().Value.ShouldBe("KUECHE-ROSENHEIM");
        extras.LineProperties.ShouldContain(p => p.Name == "Chargennummer" && p.Value == "L-2026-2991" && p.Line == 1);
        var root = Reader.Load(Fixtures.Bytes(Fixtures.FacturXEn16931));
        foreach (var property in extras.LineProperties)
        {
            XmlPath.TryParse(property.Path, out _).ShouldNotBeNull().First(root).ShouldNotBeNull().Value.ShouldBe(property.Value);
        }
    }

    [Fact]
    public void Every_fixture_reads_without_error()
    {
        foreach (var fixture in Fixtures.All)
        {
            Should.NotThrow(() => Extras(fixture), fixture);
        }
    }

    [Fact]
    public void The_walker_matches_by_local_name_and_takes_positions_filters_and_attributes()
    {
        var root = Reader.Load(Fixtures.Utf8("""
            <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2" xmlns:cac="urn:a" xmlns:cbc="urn:b">
              <cac:AccountingCustomerParty><cac:Party>
                <cac:PartyIdentification><cbc:ID schemeID="0088">4000001000005</cbc:ID></cac:PartyIdentification>
                <cac:PartyIdentification><cbc:ID>123 456789</cbc:ID></cac:PartyIdentification>
              </cac:Party></cac:AccountingCustomerParty>
              <cac:InvoiceLine><cbc:ID>1</cbc:ID><cac:Item><cac:AdditionalItemProperty><cbc:Name>Farbe</cbc:Name><cbc:Value>rot</cbc:Value></cac:AdditionalItemProperty></cac:Item></cac:InvoiceLine>
              <cac:InvoiceLine><cbc:ID>2</cbc:ID><cac:Item><cac:AdditionalItemProperty><cbc:Name>Farbe</cbc:Name><cbc:Value>blau</cbc:Value></cac:AdditionalItemProperty></cac:Item></cac:InvoiceLine>
            </Invoice>
            """));

        Select(root, "cac:AccountingCustomerParty/cac:Party/cac:PartyIdentification/cbc:ID").ShouldBe(["4000001000005", "123 456789"]);
        Select(root, "/Invoice/AccountingCustomerParty/Party/PartyIdentification[2]/ID").ShouldBe(["123 456789"]);
        Select(root, "x:AccountingCustomerParty/x:Party/x:PartyIdentification[1]/x:ID/@schemeID").ShouldBe(["0088"]);
        Select(root, "cac:InvoiceLine/cac:Item/cac:AdditionalItemProperty[cbc:Name='Farbe']/cbc:Value").ShouldBe(["rot", "blau"]);
        Select(root, "cac:InvoiceLine[2]/cac:Item/cac:AdditionalItemProperty[cbc:Name=\"Farbe\"]/cbc:Value").ShouldBe(["blau"]);
        Select(root, "cac:InvoiceLine/cac:Missing").ShouldBeEmpty();
        XmlPath.TryParse("cac:InvoiceLine[2]/cbc:ID", out _)!.First(root)!.Path.ShouldBe("Invoice/InvoiceLine[2]/ID[1]");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("a[0]")]
    [InlineData("a[b=c]")]
    [InlineData("//a")]
    [InlineData("a/@")]
    [InlineData("a/b()")]
    [InlineData("a[position()<2]")]
    public void Expressions_outside_the_walker_are_refused(string path)
    {
        XmlPath.TryParse(path, out var error).ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void An_oversized_or_DTD_document_is_refused_like_the_parser_refuses_it()
    {
        var dtd = Fixtures.Utf8("""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e "boom">]><Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2">&e;</Invoice>""");
        Should.Throw<EInvoiceParseException>(() => Reader.Load(dtd));
        var big = new EInvoiceExtrasReader(new EInvoiceParserOptions { MaxDocumentBytes = 10 });
        Should.Throw<EInvoiceParseException>(() => big.Load(Fixtures.Bytes(Fixtures.PeppolInvoice)));
    }

    private static List<string> Select(System.Xml.Linq.XElement root, string path) =>
        [.. XmlPath.TryParse(path, out var error).ShouldNotBeNull(error).Select(root).Select(h => h.Value)];
}
