using System.Reflection;
using System.Text;
using DocProc.Application.Abstractions;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation;
using DocProc.Extraction.EInvoice;

namespace DocProc.EInvoice.Tests;

/// <summary>The committed sample documents and the tenant they are addressed to.</summary>
internal static class Fixtures
{
    public const string PeppolInvoice = "peppol-bis3-invoice.xml";
    public const string PeppolCreditNote = "peppol-bis3-credit-note.xml";
    public const string FacturXEn16931 = "facturx-en16931-invoice.xml";
    public const string ZugferdBasic = "zugferd-basic-invoice.xml";
    public const string XRechnungUbl = "xrechnung-ubl-invoice.xml";

    public static readonly string[] All = [PeppolInvoice, PeppolCreditNote, FacturXEn16931, ZugferdBasic, XRechnungUbl];

    public static readonly DateOnly Today = new(2026, 10, 30);

    public static readonly TenantContext Tenant = new("Gasthaus zur Post GmbH", "DE123456789", "EUR", ["Gasthaus zur Post"]);

    public static ValidationContext Context => ValidationContext.For(Today, Tenant);

    public static byte[] Bytes(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static string Text(string name) => Encoding.UTF8.GetString(Bytes(name));

    public static byte[] Utf8(string xml) => Encoding.UTF8.GetBytes(xml);

    public static Invoice Parse(string name) => Parse(Bytes(name));

    public static Invoice Parse(byte[] xml)
    {
        var parser = new EInvoiceParser();
        var detection = parser.Detect(xml).ShouldNotBeNull();
        return parser.Parse(xml, detection);
    }

    public static EInvoiceDetection? Detect(string name) => EInvoiceDetector.Detect(Bytes(name));
}
