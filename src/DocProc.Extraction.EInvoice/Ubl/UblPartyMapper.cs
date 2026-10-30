using System.Xml.Linq;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Ubl;

/// <summary>UBL AccountingSupplierParty / AccountingCustomerParty to the canonical <see cref="Party"/>.</summary>
internal static class UblPartyMapper
{
    private const string VatScheme = "VAT";
    private const string TaxNumberScheme = "FC";
    private const string EmailEndpointScheme = "EM";

    public static FieldValue<Party> Supplier(XElement root) =>
        Map(root.Path("AccountingSupplierParty", "Party"), root.Path("PaymentMeans", "PayeeFinancialAccount"));

    public static FieldValue<Party> Customer(XElement root) =>
        Map(
            root.Path("AccountingCustomerParty", "Party"),
            root.Path("PaymentMeans", "PayerFinancialAccount") ?? root.Path("PaymentMeans", "PaymentMandate", "PayerFinancialAccount"));

    private static FieldValue<Party> Map(XElement? party, XElement? account)
    {
        if (party is null)
        {
            return FieldValue<Party>.Missing();
        }

        var name = party.Path("PartyLegalEntity", "RegistrationName").Text() ?? party.Path("PartyName", "Name").Text();
        var endpoint = party.Child("EndpointID");
        var value = new Party(
            Name: name,
            VatId: CompanyId(party, VatScheme),
            TaxId: CompanyId(party, TaxNumberScheme),
            RegistrationNumber: party.Path("PartyLegalEntity", "CompanyID").Text(),
            Address: Address(party.Child("PostalAddress")),
            Email: party.Path("Contact", "ElectronicMail").Text() ?? (endpoint.Attr("schemeID") == EmailEndpointScheme ? endpoint.Text() : null),
            Phone: party.Path("Contact", "Telephone").Text(),
            Iban: account.Child("ID").Text(),
            Bic: account.Path("FinancialInstitutionBranch", "ID").Text());
        return XmlFields.Extracted(value, name);
    }

    private static string? CompanyId(XElement party, string scheme) =>
        party.Children("PartyTaxScheme")
            .FirstOrDefault(s => s.Path("TaxScheme", "ID").Text() == scheme)
            .Child("CompanyID")
            .Text();

    private static Address? Address(XElement? address)
    {
        if (address is null)
        {
            return null;
        }

        return new Address(
            Line1: address.Child("StreetName").Text(),
            Line2: address.Child("AdditionalStreetName").Text() ?? address.Path("AddressLine", "Line").Text(),
            PostalCode: address.Child("PostalZone").Text(),
            City: address.Child("CityName").Text(),
            Region: address.Child("CountrySubentity").Text(),
            CountryCode: address.Path("Country", "IdentificationCode").Text());
    }
}
