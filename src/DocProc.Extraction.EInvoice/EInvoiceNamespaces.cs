namespace DocProc.Extraction.EInvoice;

/// <summary>
/// XML namespaces and root element names of the supported e-invoice syntaxes (docs/03-pipeline.md stage 6a):
/// OASIS UBL 2.1 Invoice / CreditNote and UN/CEFACT Cross Industry Invoice D16B.
/// </summary>
public static class EInvoiceNamespaces
{
    public const string UblInvoice = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    public const string UblCreditNote = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    public const string UblCommonAggregate = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    public const string UblCommonBasic = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    public const string CiiInvoice = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    public const string CiiReusableAggregate = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    public const string CiiUnqualifiedDataType = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";

    public const string UblInvoiceRoot = "Invoice";
    public const string UblCreditNoteRoot = "CreditNote";
    public const string CiiRoot = "CrossIndustryInvoice";
}
