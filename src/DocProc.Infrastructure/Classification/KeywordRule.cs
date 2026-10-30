using System.Text.RegularExpressions;
using DocProc.Domain.Documents;

namespace DocProc.Infrastructure.Classification;

/// <summary>What a keyword tells us about the page it is on.</summary>
internal enum KeywordKind
{
    /// <summary>A document title ("Rechnung", "Delivery note"): strong evidence, strongest in the header of the first page.</summary>
    Title,

    /// <summary>A document-number or date label ("Rechnungsnummer", "Invoice no."): moderate evidence, also carries the number.</summary>
    Label,

    /// <summary>
    /// A mention of another document ("Ihre Bestellung", "zur Rechnung", "the invoice"): weak evidence. A title
    /// preceded by an article, a possessive or a preposition is demoted to this kind.
    /// </summary>
    Reference,

    /// <summary>Phrases that say the document is not of this type ("keine Rechnung", "Rechnung folgt", "Proforma").</summary>
    Negative,
}

/// <summary>One multilingual keyword rule of the stage 4 classifier (docs/03 "Classify").</summary>
internal sealed record KeywordRule(DocumentType Type, string Language, KeywordKind Kind, Regex Pattern, double Weight);

/// <summary>
/// The rule table: German, English, Croatian, Italian and French titles, number labels and negative phrases for the
/// document types of <see cref="DocumentType"/>. Keywords match whole words only (a letter before or after breaks
/// the match), so "Rechnungsadresse" is not "Rechnung" and compounds like "Rechnungsnummer" are listed as labels.
/// </summary>
internal static class KeywordRuleSet
{
    public const double TitleWeight = 4;
    public const double LabelWeight = 1;
    public const double ReferenceWeight = 0.5;
    public const double NegativeWeight = -4;

    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>A number marker right after a title turns it into a label ("Rechnung Nr. 42", "Facture n° 7").</summary>
    public static readonly Regex NumberMarkerAfterTitle = new(@"\G\s*[:\-#]?\s*(?:nr|no|n°|nº|nummer|number|num|broj|br|numero|numéro|n)(?!\p{L})\.?", Flags);

    /// <summary>The value after a label; document numbers carry a digit, so "Rechnungsnummer und Datum" yields none.</summary>
    public static readonly Regex NumberValueAfterLabel = new(@"\G[\s:#.]*(?<value>(?=[\p{L}\d\-/._]*\d)[\p{L}\d][\p{L}\d\-/._]{1,})", Flags);

    /// <summary>An article, possessive or preposition before a title means the text refers to another document ("Ihre Bestellung", "the invoice", "die Rechnung folgt").</summary>
    public static readonly Regex ReferencePrefix = new(
        @"(?<!\p{L})(?:die|der|das|eine|einer|einem|einen|diese|dieser|ihre|ihrer|unsere|unserer|zur|zu|laut|gemäß|gemaess|als|your|our|the|this|per|to|of|votre|notre|la|le|les|une|un|cette|vostro|vostra|nostro|nostra|della|alla|il|una|vaša|vaše|naša|naše|za|prema)\s+$",
        Flags);

    public static readonly IReadOnlyList<KeywordRule> Rules = Build().ToList();

    private static IEnumerable<KeywordRule> Build()
    {
        // Invoice
        yield return Title(DocumentType.Invoice, DocumentLanguages.German, "Rechnung", "Rechnungen");
        yield return Title(DocumentType.Invoice, DocumentLanguages.English, "Invoice", "Invoices");
        yield return Title(DocumentType.Invoice, DocumentLanguages.Croatian, "Račun", "Faktura");
        yield return Title(DocumentType.Invoice, DocumentLanguages.Italian, "Fattura");
        yield return Title(DocumentType.Invoice, DocumentLanguages.French, "Facture");
        yield return Label(DocumentType.Invoice, DocumentLanguages.German, "Rechnungsnummer", "Rechnungs-Nr", "Rechnungsnr", "Rechnungsdatum", "Rechnungs-Datum");
        yield return Label(DocumentType.Invoice, DocumentLanguages.English, "Invoice number", "Invoice no", "Invoice #", "Invoice date", "Invoice ID");
        yield return Label(DocumentType.Invoice, DocumentLanguages.Croatian, "Broj računa", "Datum računa");
        yield return Label(DocumentType.Invoice, DocumentLanguages.Italian, "Numero fattura", "Data fattura");
        yield return Label(DocumentType.Invoice, DocumentLanguages.French, "Numéro de facture", "N° de facture", "Date de facture");
        yield return Negative(DocumentType.Invoice, DocumentLanguages.German, "keine Rechnung", "Rechnung folgt", "nicht als Rechnung", "Proforma", "Pro-forma");
        yield return Negative(DocumentType.Invoice, DocumentLanguages.English, "not an invoice", "invoice to follow", "invoice will follow", "Proforma", "Pro-forma");
        yield return Negative(DocumentType.Invoice, DocumentLanguages.Croatian, "nije račun", "Predračun");
        yield return Negative(DocumentType.Invoice, DocumentLanguages.Italian, "non è una fattura", "Proforma", "Pro-forma");
        yield return Negative(DocumentType.Invoice, DocumentLanguages.French, "n'est pas une facture", "Proforma", "Pro-forma");

        // Credit note
        yield return Title(DocumentType.CreditNote, DocumentLanguages.German, "Gutschrift", "Gutschriften", "Rechnungskorrektur", "Stornorechnung", "Storno-Rechnung");
        yield return Title(DocumentType.CreditNote, DocumentLanguages.English, "Credit note", "Credit memo", "Credit memorandum");
        yield return Title(DocumentType.CreditNote, DocumentLanguages.Croatian, "Odobrenje", "Knjižno odobrenje", "Storno račun", "Storno");
        yield return Title(DocumentType.CreditNote, DocumentLanguages.Italian, "Nota di credito", "Nota credito", "Nota di accredito");
        yield return Title(DocumentType.CreditNote, DocumentLanguages.French, "Avoir", "Note de crédit", "Facture d'avoir");
        yield return Label(DocumentType.CreditNote, DocumentLanguages.German, "Gutschriftsnummer", "Gutschrift-Nr", "Gutschrifts-Nr", "Gutschriftnummer");
        yield return Label(DocumentType.CreditNote, DocumentLanguages.English, "Credit note number", "Credit note no", "Credit memo no", "Credit memo number");
        yield return Label(DocumentType.CreditNote, DocumentLanguages.Croatian, "Broj odobrenja");
        yield return Label(DocumentType.CreditNote, DocumentLanguages.Italian, "Numero nota di credito");
        yield return Label(DocumentType.CreditNote, DocumentLanguages.French, "Numéro d'avoir");

        // Delivery note
        yield return Title(DocumentType.DeliveryNote, DocumentLanguages.German, "Lieferschein", "Lieferscheine", "Warenbegleitschein", "Packzettel");
        yield return Title(DocumentType.DeliveryNote, DocumentLanguages.English, "Delivery note", "Delivery slip", "Packing slip", "Packing list", "Dispatch note", "Shipping note");
        yield return Title(DocumentType.DeliveryNote, DocumentLanguages.Croatian, "Otpremnica", "Dostavnica");
        yield return Title(DocumentType.DeliveryNote, DocumentLanguages.Italian, "Bolla di consegna", "Documento di trasporto", "DDT", "Bolla di accompagnamento");
        yield return Title(DocumentType.DeliveryNote, DocumentLanguages.French, "Bon de livraison", "Bordereau de livraison", "Bordereau d'expédition");
        yield return Label(DocumentType.DeliveryNote, DocumentLanguages.German, "Lieferscheinnummer", "Lieferschein-Nr", "Lieferscheinnr", "Lieferscheindatum");
        yield return Label(DocumentType.DeliveryNote, DocumentLanguages.English, "Delivery note number", "Delivery note no", "Delivery no", "Packing slip no");
        yield return Label(DocumentType.DeliveryNote, DocumentLanguages.Croatian, "Broj otpremnice");
        yield return Label(DocumentType.DeliveryNote, DocumentLanguages.Italian, "Numero DDT", "Numero bolla");
        yield return Label(DocumentType.DeliveryNote, DocumentLanguages.French, "Numéro de bon de livraison", "N° BL");

        // Purchase order
        yield return Title(DocumentType.PurchaseOrder, DocumentLanguages.German, "Bestellung", "Bestellschein", "Einkaufsbestellung");
        yield return Title(DocumentType.PurchaseOrder, DocumentLanguages.English, "Purchase order", "Order form");
        yield return Title(DocumentType.PurchaseOrder, DocumentLanguages.Croatian, "Narudžba", "Narudžbenica", "Nalog za nabavu");
        yield return Title(DocumentType.PurchaseOrder, DocumentLanguages.Italian, "Ordine", "Ordine di acquisto", "Ordine d'acquisto", "Buono d'ordine");
        yield return Title(DocumentType.PurchaseOrder, DocumentLanguages.French, "Bon de commande", "Commande", "Ordre d'achat");
        yield return Label(DocumentType.PurchaseOrder, DocumentLanguages.German, "Bestellnummer", "Bestell-Nr", "Bestellnr", "Bestelldatum");
        yield return Label(DocumentType.PurchaseOrder, DocumentLanguages.English, "Purchase order number", "Purchase order no", "PO number", "PO no", "PO #", "Order number", "Order no");
        yield return Label(DocumentType.PurchaseOrder, DocumentLanguages.Croatian, "Broj narudžbe");
        yield return Label(DocumentType.PurchaseOrder, DocumentLanguages.Italian, "Numero ordine");
        yield return Label(DocumentType.PurchaseOrder, DocumentLanguages.French, "Numéro de commande", "N° de commande");

        // Statement
        yield return Title(DocumentType.Statement, DocumentLanguages.German, "Kontoauszug", "Offene Posten", "Offene-Posten-Liste", "Saldenbestätigung", "Saldenliste", "Kontoübersicht");
        yield return Title(DocumentType.Statement, DocumentLanguages.English, "Statement", "Statement of account", "Account statement", "Open items");
        yield return Title(DocumentType.Statement, DocumentLanguages.Croatian, "Izvod", "Izvod otvorenih stavki", "Otvorene stavke", "IOS obrazac");
        yield return Title(DocumentType.Statement, DocumentLanguages.Italian, "Estratto conto", "Partite aperte");
        yield return Title(DocumentType.Statement, DocumentLanguages.French, "Relevé de compte", "Relevé", "Relevé de factures");

        // Reminder
        yield return Title(DocumentType.Reminder, DocumentLanguages.German, "Mahnung", "Zahlungserinnerung", "Mahnbescheid");
        yield return Title(DocumentType.Reminder, DocumentLanguages.English, "Reminder", "Payment reminder", "Overdue notice", "Final notice", "Dunning letter", "Dunning notice", "Past due notice");
        yield return Title(DocumentType.Reminder, DocumentLanguages.Croatian, "Opomena", "Podsjetnik na plaćanje");
        yield return Title(DocumentType.Reminder, DocumentLanguages.Italian, "Sollecito", "Sollecito di pagamento", "Avviso di pagamento scaduto");
        yield return Title(DocumentType.Reminder, DocumentLanguages.French, "Rappel", "Rappel de paiement", "Relance", "Lettre de relance", "Mise en demeure");
    }

    private static KeywordRule Title(DocumentType type, string language, params string[] phrases) =>
        new(type, language, KeywordKind.Title, Compile(phrases), TitleWeight);

    private static KeywordRule Label(DocumentType type, string language, params string[] phrases) =>
        new(type, language, KeywordKind.Label, Compile(phrases), LabelWeight);

    private static KeywordRule Negative(DocumentType type, string language, params string[] phrases) =>
        new(type, language, KeywordKind.Negative, Compile(phrases), NegativeWeight);

    /// <summary>Whole-word alternation; spaces match any whitespace run, hyphens are optional ("Rechnungs-Nr" and "Rechnungs Nr").</summary>
    private static Regex Compile(IEnumerable<string> phrases)
    {
        var alternatives = phrases
            .OrderByDescending(p => p.Length)
            .Select(p => Regex.Escape(p).Replace(@"\ ", @"\s+", StringComparison.Ordinal).Replace("-", @"[-\s]?", StringComparison.Ordinal));
        return new Regex($@"(?<!\p{{L}})(?:{string.Join('|', alternatives)})(?!\p{{L}})", Flags);
    }
}
