using System.Xml.Linq;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice.Cii;

/// <summary>CII SellerTradeParty / BuyerTradeParty to the canonical <see cref="Party"/>.</summary>
internal static class CiiPartyMapper
{
    private const string VatScheme = "VA";
    private const string TaxNumberScheme = "FC";
    private const string EmailEndpointScheme = "EM";

    public static FieldValue<Party> Supplier(XElement? agreement, XElement? settlement) =>
        Map(agreement.Child("SellerTradeParty"), CiiPaymentMeans.PayeeAccount(CiiPaymentMeans.All(settlement)));

    public static FieldValue<Party> Customer(XElement? agreement, XElement? settlement) =>
        Map(agreement.Child("BuyerTradeParty"), CiiPaymentMeans.PayerAccount(CiiPaymentMeans.All(settlement)));

    /// <summary>
    /// The party's electronic address (URIUniversalCommunication/URIID, BT-34/BT-49) is an e-mail only under EAS
    /// scheme EM, as in the UBL mapper; under any other scheme (0204 Leitweg-ID, 0088 GLN, 9930 VAT id) it is a
    /// routing identifier the canonical <see cref="Party"/> has no slot for, and it is not mistaken for an e-mail.
    /// </summary>
    private static FieldValue<Party> Map(XElement? party, BankAccount account)
    {
        if (party is null)
        {
            return FieldValue<Party>.Missing();
        }

        var name = party.Child("Name").Text();
        var contact = party.Child("DefinedTradeContact");
        var endpoint = party.Path("URIUniversalCommunication", "URIID");
        var value = new Party(
            Name: name,
            VatId: TaxRegistration(party, VatScheme),
            TaxId: TaxRegistration(party, TaxNumberScheme),
            RegistrationNumber: party.Path("SpecifiedLegalOrganization", "ID").Text(),
            Address: Address(party.Child("PostalTradeAddress")),
            Email: contact.Path("EmailURIUniversalCommunication", "URIID").Text() ?? (endpoint.Attr("schemeID") == EmailEndpointScheme ? endpoint.Text() : null),
            Phone: contact.Path("TelephoneUniversalCommunication", "CompleteNumber").Text(),
            Iban: account.Iban,
            Bic: account.Bic);
        return XmlFields.Extracted(value, name);
    }

    private static string? TaxRegistration(XElement party, string scheme) =>
        party.Children("SpecifiedTaxRegistration")
            .Select(r => r.Child("ID"))
            .FirstOrDefault(id => id.Attr("schemeID") == scheme)
            .Text();

    private static Address? Address(XElement? address)
    {
        if (address is null)
        {
            return null;
        }

        return new Address(
            Line1: address.Child("LineOne").Text(),
            Line2: address.Child("LineTwo").Text() ?? address.Child("LineThree").Text(),
            PostalCode: address.Child("PostcodeCode").Text(),
            City: address.Child("CityName").Text(),
            Region: address.Child("CountrySubDivisionName").Text(),
            CountryCode: address.Child("CountryID").Text());
    }
}
