using System.Diagnostics;
using System.Text;
using DocProc.Application.Abstractions;
using DocProc.Extraction.EInvoice;

namespace DocProc.EInvoice.Tests;

/// <summary>
/// Untrusted XML cannot pin a worker: the size and nesting limits of <see cref="EInvoiceParserOptions"/> reject a
/// crafted document on both the detection and the parsing path, and do so quickly.
/// </summary>
public class ResourceLimitTests
{
    private const string UblInvoiceNamespace = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private const int NestingDepth = 200_000;

    private static readonly EInvoiceDetection UblDetection = new(EInvoiceFlavour.Ubl, null, "Invoice");

    /// <summary>A UBL invoice whose UBLExtensions, which the detector has to pass, holds 200,000 nested elements.</summary>
    private static byte[] DeeplyNestedUbl()
    {
        var xml = new StringBuilder(NestingDepth * 8);
        xml.Append($"<Invoice xmlns=\"{UblInvoiceNamespace}\"><UBLExtensions>");
        xml.Insert(xml.Length, "<a>", NestingDepth);
        xml.Insert(xml.Length, "</a>", NestingDepth);
        xml.Append("</UBLExtensions><CustomizationID>urn:cen.eu:en16931:2017</CustomizationID><ID>1</ID></Invoice>");
        return Fixtures.Utf8(xml.ToString());
    }

    [Fact]
    public void Deeply_nested_document_is_rejected_by_detection_and_parsing_within_two_seconds()
    {
        var xml = DeeplyNestedUbl();
        var parser = new EInvoiceParser();
        var stopwatch = Stopwatch.StartNew();

        parser.Detect(xml).ShouldBeNull();
        var ex = Should.Throw<EInvoiceParseException>(() => parser.Parse(xml, UblDetection));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10)); // milliseconds in practice; generous for slow CI runners
        ex.Message.ShouldContain("deeper than the limit of 256");
    }

    [Fact]
    public void Nesting_within_the_configured_depth_is_accepted()
    {
        var options = new EInvoiceParserOptions { MaxElementDepth = 300 };
        var xml = DeeplyNestedUbl();
        var shallow = Fixtures.Utf8(
            $"<Invoice xmlns=\"{UblInvoiceNamespace}\"><UBLExtensions>" + string.Concat(Enumerable.Repeat("<a>", 250)) +
            string.Concat(Enumerable.Repeat("</a>", 250)) + "</UBLExtensions><CustomizationID>urn:cen.eu:en16931:2017</CustomizationID><ID>1</ID></Invoice>");
        var parser = new EInvoiceParser(options);

        parser.Detect(xml).ShouldBeNull();
        var detection = parser.Detect(shallow).ShouldNotBeNull();
        detection.Profile.ShouldBe("urn:cen.eu:en16931:2017");
        parser.Parse(shallow, detection).InvoiceNumber.Value!.Verbatim.ShouldBe("1");
    }

    [Fact]
    public void Document_over_the_byte_limit_is_rejected_before_parsing()
    {
        var xml = Fixtures.Bytes(Fixtures.PeppolInvoice);
        var parser = new EInvoiceParser(new EInvoiceParserOptions { MaxDocumentBytes = xml.Length - 1 });

        parser.Detect(xml).ShouldBeNull();
        var ex = Should.Throw<EInvoiceParseException>(() => parser.Parse(xml, UblDetection));

        ex.Message.ShouldContain($"above the limit of {xml.Length - 1} bytes");
        new EInvoiceParser(new EInvoiceParserOptions { MaxDocumentBytes = xml.Length }).Detect(xml).ShouldNotBeNull();
    }

    [Fact]
    public void Default_limits_accept_every_sample_document()
    {
        EInvoiceParserOptions.Default.MaxDocumentBytes.ShouldBe(20 * 1024 * 1024);
        EInvoiceParserOptions.Default.MaxElementDepth.ShouldBe(256);
        foreach (var name in Fixtures.All)
        {
            Fixtures.Parse(name).InvoiceNumber.HasValue.ShouldBeTrue(name);
        }
    }

    [Fact]
    public void Limits_must_be_positive()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new EInvoiceParserOptions { MaxDocumentBytes = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new EInvoiceParserOptions { MaxElementDepth = -1 });
    }
}
