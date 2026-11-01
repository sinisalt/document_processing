using DocProc.Application.Abstractions;
using DocProc.Extraction.EInvoice;

namespace DocProc.EInvoice.Tests;

public class DetectionTests
{
    private const string PeppolProfile = "urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0";

    [Theory]
    [InlineData(Fixtures.PeppolInvoice, EInvoiceFlavour.Ubl, PeppolProfile, "Invoice")]
    [InlineData(Fixtures.PeppolCreditNote, EInvoiceFlavour.Ubl, PeppolProfile, "CreditNote")]
    [InlineData(Fixtures.FacturXEn16931, EInvoiceFlavour.Cii, "urn:cen.eu:en16931:2017", "CrossIndustryInvoice")]
    [InlineData(Fixtures.ZugferdBasic, EInvoiceFlavour.Cii, "urn:factur-x.eu:1p0:basic", "CrossIndustryInvoice")]
    [InlineData(Fixtures.XRechnungUbl, EInvoiceFlavour.Ubl, "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", "Invoice")]
    public void Detects_flavour_profile_and_root_of_each_fixture(string fixture, EInvoiceFlavour flavour, string profile, string root)
    {
        var detection = Fixtures.Detect(fixture).ShouldNotBeNull();

        detection.Flavour.ShouldBe(flavour);
        detection.Profile.ShouldBe(profile);
        detection.RootElement.ShouldBe(root);
    }

    [Fact]
    public void Parser_interface_detects_the_same_way_as_the_detector()
    {
        var bytes = Fixtures.Bytes(Fixtures.FacturXEn16931);

        new EInvoiceParser().Detect(bytes).ShouldBe(EInvoiceDetector.Detect(bytes));
    }

    [Fact]
    public void Rejects_a_document_with_a_doctype_even_when_the_root_is_an_invoice()
    {
        var xxe = Fixtures.Utf8(
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE Invoice [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>" +
            "<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\"><ID>&xxe;</ID></Invoice>");

        EInvoiceDetector.Detect(xxe).ShouldBeNull();
    }

    [Fact]
    public void Rejects_a_document_with_an_internal_only_doctype()
    {
        var doctype = Fixtures.Utf8(
            "<!DOCTYPE CrossIndustryInvoice>" +
            "<rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"/>");

        EInvoiceDetector.Detect(doctype).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj")]
    [InlineData("Rechnung Nr. 4711 vom 27.10.2026")]
    [InlineData("{\"invoice\": \"RE-1\"}")]
    [InlineData("<")]
    public void Rejects_non_xml_input(string text)
    {
        EInvoiceDetector.Detect(Fixtures.Utf8(text)).ShouldBeNull();
    }

    [Fact]
    public void Rejects_binary_garbage()
    {
        var bytes = new byte[512];
        new Random(42).NextBytes(bytes);

        EInvoiceDetector.Detect(bytes).ShouldBeNull();
    }

    [Theory]
    [InlineData("<Order xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Order-2\"/>")]
    [InlineData("<Invoice/>")]
    [InlineData("<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-1\"/>")]
    [InlineData("<CrossIndustryInvoice xmlns=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:13\"/>")]
    [InlineData("<rsm:CrossIndustryDocument xmlns:rsm=\"urn:ferd:CrossIndustryDocument:invoice:1p0\"/>")]
    [InlineData("<html><body>Invoice</body></html>")]
    public void Rejects_xml_that_is_not_a_supported_e_invoice(string xml)
    {
        EInvoiceDetector.Detect(Fixtures.Utf8(xml)).ShouldBeNull();
    }

    [Fact]
    public void Ubl_without_customization_id_is_detected_with_a_null_profile()
    {
        var xml = Fixtures.Utf8(
            "<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\" xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\">" +
            "<cbc:ID>1</cbc:ID><cbc:CustomizationID>too late, not in the preamble</cbc:CustomizationID></Invoice>");

        var detection = EInvoiceDetector.Detect(xml).ShouldNotBeNull();

        detection.Flavour.ShouldBe(EInvoiceFlavour.Ubl);
        detection.Profile.ShouldBeNull();
    }

    [Fact]
    public void Ubl_extensions_and_version_before_the_customization_id_are_skipped()
    {
        var xml = Fixtures.Utf8(
            "<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\"" +
            " xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\"" +
            " xmlns:ext=\"urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2\">" +
            "<ext:UBLExtensions><ext:UBLExtension><ext:ExtensionContent><sig><data>x</data></sig></ext:ExtensionContent></ext:UBLExtension></ext:UBLExtensions>" +
            "<cbc:UBLVersionID>2.1</cbc:UBLVersionID>" +
            "<cbc:CustomizationID> urn:test:profile </cbc:CustomizationID>" +
            "<cbc:ID>1</cbc:ID></Invoice>");

        EInvoiceDetector.Detect(xml).ShouldNotBeNull().Profile.ShouldBe("urn:test:profile");
    }

    [Fact]
    public void Cii_without_a_document_context_is_detected_with_a_null_profile()
    {
        var xml = Fixtures.Utf8(
            "<rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\">" +
            "<rsm:ExchangedDocument/></rsm:CrossIndustryInvoice>");

        var detection = EInvoiceDetector.Detect(xml).ShouldNotBeNull();

        detection.Flavour.ShouldBe(EInvoiceFlavour.Cii);
        detection.Profile.ShouldBeNull();
        detection.RootElement.ShouldBe("CrossIndustryInvoice");
    }

    [Fact]
    public void A_recognised_root_with_a_broken_body_is_still_detected_so_parse_can_report_it()
    {
        var truncated = Fixtures.Utf8(
            "<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\" xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\">" +
            "<cbc:CustomizationID>urn:x</cbc:Customiz");

        var detection = EInvoiceDetector.Detect(truncated).ShouldNotBeNull();

        detection.Flavour.ShouldBe(EInvoiceFlavour.Ubl);
        detection.Profile.ShouldBeNull();
        Should.Throw<EInvoiceParseException>(() => new EInvoiceParser().Parse(truncated, detection));
    }

    [Fact]
    public void Detection_works_on_a_slice_of_a_larger_buffer()
    {
        var fixture = Fixtures.Bytes(Fixtures.PeppolInvoice);
        var padded = new byte[fixture.Length + 64];
        fixture.CopyTo(padded, 32);

        var detection = EInvoiceDetector.Detect(new ReadOnlyMemory<byte>(padded, 32, fixture.Length)).ShouldNotBeNull();

        detection.Flavour.ShouldBe(EInvoiceFlavour.Ubl);
        detection.Profile.ShouldBe(PeppolProfile);
    }
}
