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

    public static FieldValue<Party> Supplier(XElement? agreement, XElement? settlement)
    {
        var means = settlement.Child("SpecifiedTradeSettlementPaymentMeans");
        return Map(
            agreement.Child("SellerTradeParty"),
            iban: means.Path("PayeePartyCreditorFinancialAccount", "IBANID").Text(),
            bic: means.Path("PayeeSpecifiedCreditorFinancialInstitution", "BICID").Text());
    }

    public static FieldValue<Party> Customer(XElement? agreement, XElement? settlement)
    {
        var means = settlement.Child("SpecifiedTradeSettlementPaymentMeans");
        return Map(
            agreement.Child("BuyerTradeParty"),
            iban: means.Path("PayerPartyDebtorFinancialAccount", "IBANID").Text(),
            bic: null);
    }

    private static FieldValue<Party> Map(XElement? party, string? iban, string? bic)
    {
        if (party is null)
        {
            return FieldValue<Party>.Missing();
        }

        var name = party.Child("Name").Text();
        var contact = party.Child("DefinedTradeContact");
        var value = new Party(
            Name: name,
            VatId: TaxRegistration(party, VatScheme),
            TaxId: TaxRegistration(party, TaxNumberScheme),
            RegistrationNumber: party.Path("SpecifiedLegalOrganization", "ID").Text(),
            Address: Address(party.Child("PostalTradeAddress")),
            Email: contact.Path("EmailURIUniversalCommunication", "URIID").Text() ?? party.Path("URIUniversalCommunication", "URIID").Text(),
            Phone: contact.Path("TelephoneUniversalCommunication", "CompleteNumber").Text(),
            Iban: iban,
            Bic: bic);
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
