using System.Globalization;
using System.Xml.Linq;
using DocProc.Application.Abstractions;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice;

/// <summary>A value of an e-invoice extra with the element path it was read from.</summary>
public sealed record EInvoiceExtraValue(string Value, string Path, string? SchemeId = null);

/// <summary>A name/value property of an invoice line (UBL <c>AdditionalItemProperty</c>, CII <c>ApplicableProductCharacteristic</c>).</summary>
public sealed record EInvoiceLineProperty(int Line, string? LineId, string Name, string Value, string Path);

/// <summary>
/// What the e-invoice parsers read and the canonical invoice drops (docs/11 §9.3 <c>EInvoiceExtras</c>): the buyer
/// reference (BT-10, the XRechnung Leitweg-ID), the accounting cost (BT-19), the project reference (BT-11), the
/// contract reference (BT-12), the party identifications of the supplier (BT-29) and the customer (BT-46), and every
/// line property. Supplier layouts bind them to fields with <c>xml-path</c> rules; each value carries its element path.
/// </summary>
public sealed record EInvoiceExtras(
    EInvoiceExtraValue? BuyerReference,
    EInvoiceExtraValue? AccountingCost,
    EInvoiceExtraValue? ProjectReference,
    EInvoiceExtraValue? ContractReference,
    IReadOnlyList<EInvoiceExtraValue> SupplierPartyIdentifications,
    IReadOnlyList<EInvoiceExtraValue> CustomerPartyIdentifications,
    IReadOnlyList<EInvoiceLineProperty> LineProperties)
{
    public static EInvoiceExtras Empty { get; } = new(null, null, null, null, [], [], []);
}

/// <summary>
/// Reads <see cref="EInvoiceExtras"/> and evaluates <see cref="XmlPath"/>s over an e-invoice, with the same security
/// limits as the parser (no DTD, size and depth caps of <see cref="EInvoiceParserOptions"/>).
/// </summary>
public sealed class EInvoiceExtrasReader(EInvoiceParserOptions options)
{
    public EInvoiceExtrasReader()
        : this(EInvoiceParserOptions.Default)
    {
    }

    /// <summary>The root element of the XML, read securely; <see cref="EInvoiceParseException"/> for malformed or oversized XML.</summary>
    public XElement Load(ReadOnlyMemory<byte> xml) =>
        SecureXml.Load(xml, options).Root ?? throw new EInvoiceParseException("The e-invoice XML has no root element.");

    /// <summary>The values an <c>xml-path</c> rule selects in <paramref name="xml"/>.</summary>
    public IReadOnlyList<XmlPathHit> Select(ReadOnlyMemory<byte> xml, XmlPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Select(Load(xml));
    }

    public EInvoiceExtras Read(ReadOnlyMemory<byte> xml, EInvoiceDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        var root = Load(xml);
        return detection.Flavour switch
        {
            EInvoiceFlavour.Ubl => Ubl(root),
            EInvoiceFlavour.Cii => Cii(root),
            _ => EInvoiceExtras.Empty,
        };
    }

    private static EInvoiceExtras Ubl(XElement root)
    {
        var name = root.Name.LocalName;
        var lineName = name == EInvoiceNamespaces.UblCreditNoteRoot ? "CreditNoteLine" : "InvoiceLine";
        var lines = new List<EInvoiceLineProperty>();
        var number = 0;
        foreach (var line in root.Children(lineName))
        {
            number++;
            var property = 0;
            foreach (var p in line.Path("Item").Children("AdditionalItemProperty"))
            {
                property++;
                if (p.Child("Name").Text() is { } propertyName && p.Child("Value").Text() is { } value)
                {
                    lines.Add(new EInvoiceLineProperty(number, line.Child("ID").Text(), propertyName, value, $"{name}/{lineName}[{N(number)}]/Item[1]/AdditionalItemProperty[{N(property)}]/Value[1]"));
                }
            }
        }

        return new EInvoiceExtras(
            Value(root.Child("BuyerReference"), $"{name}/BuyerReference[1]"),
            Value(root.Child("AccountingCost"), $"{name}/AccountingCost[1]"),
            Value(root.Path("ProjectReference", "ID"), $"{name}/ProjectReference[1]/ID[1]"),
            Value(root.Path("ContractDocumentReference", "ID"), $"{name}/ContractDocumentReference[1]/ID[1]"),
            Identifications(root.Path("AccountingSupplierParty", "Party"), $"{name}/AccountingSupplierParty[1]/Party[1]"),
            Identifications(root.Path("AccountingCustomerParty", "Party"), $"{name}/AccountingCustomerParty[1]/Party[1]"),
            lines);
    }

    private static EInvoiceExtras Cii(XElement root)
    {
        var name = root.Name.LocalName;
        var transaction = root.Child("SupplyChainTradeTransaction");
        var agreement = transaction.Child("ApplicableHeaderTradeAgreement");
        var settlement = transaction.Child("ApplicableHeaderTradeSettlement");
        var agreementPath = $"{name}/SupplyChainTradeTransaction[1]/ApplicableHeaderTradeAgreement[1]";
        var lines = new List<EInvoiceLineProperty>();
        var number = 0;
        foreach (var line in transaction.Children("IncludedSupplyChainTradeLineItem"))
        {
            number++;
            var property = 0;
            foreach (var c in line.Path("SpecifiedTradeProduct").Children("ApplicableProductCharacteristic"))
            {
                property++;
                if (c.Child("Description").Text() is { } propertyName && c.Child("Value").Text() is { } value)
                {
                    lines.Add(new EInvoiceLineProperty(number, line.Path("AssociatedDocumentLineDocument", "LineID").Text(), propertyName, value, $"{name}/SupplyChainTradeTransaction[1]/IncludedSupplyChainTradeLineItem[{N(number)}]/SpecifiedTradeProduct[1]/ApplicableProductCharacteristic[{N(property)}]/Value[1]"));
                }
            }
        }

        return new EInvoiceExtras(
            Value(agreement.Child("BuyerReference"), $"{agreementPath}/BuyerReference[1]"),
            Value(settlement.Path("ReceivableSpecifiedTradeAccountingAccount", "ID"), $"{name}/SupplyChainTradeTransaction[1]/ApplicableHeaderTradeSettlement[1]/ReceivableSpecifiedTradeAccountingAccount[1]/ID[1]"),
            Value(agreement.Path("SpecifiedProcuringProject", "ID"), $"{agreementPath}/SpecifiedProcuringProject[1]/ID[1]"),
            Value(agreement.Path("ContractReferencedDocument", "IssuerAssignedID"), $"{agreementPath}/ContractReferencedDocument[1]/IssuerAssignedID[1]"),
            CiiIdentifications(agreement.Child("SellerTradeParty"), $"{agreementPath}/SellerTradeParty[1]"),
            CiiIdentifications(agreement.Child("BuyerTradeParty"), $"{agreementPath}/BuyerTradeParty[1]"),
            lines);
    }

    private static List<EInvoiceExtraValue> Identifications(XElement? party, string path)
    {
        var result = new List<EInvoiceExtraValue>();
        var index = 0;
        foreach (var identification in party.Children("PartyIdentification"))
        {
            index++;
            var id = identification.Child("ID");
            if (id.Text() is { } value)
            {
                result.Add(new EInvoiceExtraValue(value, $"{path}/PartyIdentification[{N(index)}]/ID[1]", id.Attr("schemeID")));
            }
        }

        return result;
    }

    private static List<EInvoiceExtraValue> CiiIdentifications(XElement? party, string path)
    {
        var result = new List<EInvoiceExtraValue>();
        var index = 0;
        foreach (var id in party.Children("ID"))
        {
            index++;
            if (id.Text() is { } value)
            {
                result.Add(new EInvoiceExtraValue(value, $"{path}/ID[{N(index)}]", id.Attr("schemeID")));
            }
        }

        index = 0;
        foreach (var id in party.Children("GlobalID"))
        {
            index++;
            if (id.Text() is { } value)
            {
                result.Add(new EInvoiceExtraValue(value, $"{path}/GlobalID[{N(index)}]", id.Attr("schemeID")));
            }
        }

        return result;
    }

    private static EInvoiceExtraValue? Value(XElement? element, string path) =>
        element.Text() is { } value ? new EInvoiceExtraValue(value, path) : null;

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
