using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Llm.Contracts;

namespace DocProc.Llm.Extraction;

/// <summary>What the mapper produces: the canonical invoice and the flags the model's answer implies.</summary>
public sealed record InvoiceMappingResult(Invoice Invoice, IReadOnlyList<DocumentFlag> Flags);

/// <summary>
/// Maps the model's verbatim answer (docs/06 "Request design") onto the canonical model (docs/02 §4.2): every value
/// becomes a <see cref="FieldValue{T}"/> with <see cref="SourceMethod.Llm"/> provenance located in the text layer and
/// a confidence derived from that locatability alone. Normalisation happens here, never in the prompt: identifiers
/// through <see cref="DocumentIdentifier"/>, units through <see cref="UnitOfMeasureParser"/>, category codes to
/// <see cref="TaxCategory"/>, amounts to <see cref="Money"/> in the printed currency or, only when none was printed,
/// the tenant default with status <see cref="FieldStatus.Inferred"/>.
/// </summary>
public sealed class InvoiceExtractionMapper(ProvenanceLocator locator, string defaultCurrency)
{
    private readonly ProvenanceLocator _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    private readonly string _defaultCurrency = CurrencyCode.Normalize(defaultCurrency ?? throw new ArgumentNullException(nameof(defaultCurrency)));
    private string _currency = CurrencyCode.Normalize(defaultCurrency);

    public InvoiceMappingResult Map(InvoiceExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        var currency = MapCurrency(extraction.Currency);
        _currency = currency.Value!;

        var invoice = new Invoice
        {
            Kind = MapKind(extraction.Kind),
            InvoiceNumber = Identifier(extraction.InvoiceNumber),
            IssueDate = Date(extraction.IssueDate),
            DeliveryDate = Date(extraction.DeliveryDate),
            ServicePeriod = MapPeriod(extraction.ServicePeriodFrom, extraction.ServicePeriodTo),
            DueDate = Date(extraction.DueDate),
            Currency = currency,
            Supplier = MapParty(extraction.Supplier),
            Customer = MapParty(extraction.Customer),
            OrderReferences = extraction.OrderReferences.Select(Identifier).Where(f => f.HasValue).ToList(),
            DeliveryNoteReferences = extraction.DeliveryNoteReferences.Select(Identifier).Where(f => f.HasValue).ToList(),
            OriginalInvoiceReference = Identifier(extraction.OriginalInvoiceReference),
            SubtotalNet = Amount(extraction.SubtotalNet),
            TaxBreakdown = extraction.TaxBreakdown.Select(MapTaxRow).OfType<TaxBreakdownLine>().ToList(),
            TotalTax = Amount(extraction.TotalTax),
            TotalGross = Amount(extraction.TotalGross),
            AmountDue = Amount(extraction.AmountDue),
            Rounding = Amount(extraction.Rounding),
            Charges = extraction.ChargesAndAllowances.Where(c => c.IsCharge).Select(MapChargeOrAllowance).OfType<ChargeOrAllowance>().ToList(),
            Allowances = extraction.ChargesAndAllowances.Where(c => !c.IsCharge).Select(MapChargeOrAllowance).OfType<ChargeOrAllowance>().ToList(),
            PaymentTerms = MapPaymentTerms(extraction.PaymentTerms),
            Notes = Text(extraction.Notes),
            Lines = extraction.Lines.Select(MapLine).ToList(),
        };

        var flags = extraction.UnreadableRegions
            .Select(r => UnreadableRegionFlag(r, invoice))
            .ToList();

        return new InvoiceMappingResult(invoice, flags);
    }

    /// <summary>
    /// A region the model could not read blocks auto-approval only when a required header field (docs/02 §4.2) came
    /// back empty, because the region may be what hid it. When every required value was returned the region covers
    /// optional content (notes, a stamp beside the totals) and the flag is a warning for the reviewer instead.
    /// </summary>
    private static DocumentFlag UnreadableRegionFlag(UnreadableRegion region, Invoice invoice)
    {
        var flag = FlagCodes.Create(FlagCodes.UnreadablePages, $"Page {region.Page}, {region.Area}: {region.Reason}");
        return RequiredHeaderFieldMissing(invoice) ? flag : flag with { Severity = FlagSeverity.Warning };
    }

    /// <summary>The required header fields of <c>RequiredFieldsRule</c>; Missing here means the model returned null.</summary>
    private static bool RequiredHeaderFieldMissing(Invoice invoice)
    {
        IFieldValue[] required =
        [
            invoice.Kind, invoice.InvoiceNumber, invoice.IssueDate, invoice.Currency, invoice.Supplier, invoice.Customer,
            invoice.SubtotalNet, invoice.TotalTax, invoice.TotalGross,
        ];
        if (required.Any(f => f.Status == FieldStatus.Missing))
        {
            return true;
        }

        if (invoice.Supplier.HasValue && string.IsNullOrWhiteSpace(invoice.Supplier.Value!.Name))
        {
            return true;
        }

        return invoice.Kind is { HasValue: true, Value: InvoiceKind.CreditNote } && invoice.OriginalInvoiceReference.Status == FieldStatus.Missing;
    }

    /// <summary>UNCL 5305 code as printed to the canonical category; unknown or absent codes stay null.</summary>
    public static TaxCategory? TaxCategoryFor(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "S" => TaxCategory.Standard,
        "Z" => TaxCategory.Zero,
        "E" => TaxCategory.Exempt,
        "AE" => TaxCategory.ReverseCharge,
        "O" => TaxCategory.OutsideScope,
        "K" => TaxCategory.IntraCommunity,
        "G" => TaxCategory.Export,
        _ => null,
    };

    private FieldValue<string> MapCurrency(string? printed)
    {
        if (string.IsNullOrWhiteSpace(printed))
        {
            // The one case where a value is derived: no currency printed, tenant default assumed. Validation flags it.
            return FieldValue.Inferred(_defaultCurrency);
        }

        var code = CurrencyCode.Normalize(printed);
        var match = _locator.LocateAny(ValueVariants.Currency(code));
        return FieldValue.Extracted(code, match.ToSource(), match.Confidence);
    }

    private FieldValue<InvoiceKind> MapKind(ExtractedInvoiceKind? kind)
    {
        if (kind is null)
        {
            return FieldValue<InvoiceKind>.Missing();
        }

        var mapped = kind switch
        {
            ExtractedInvoiceKind.CreditNote => InvoiceKind.CreditNote,
            ExtractedInvoiceKind.Proforma => InvoiceKind.Proforma,
            _ => InvoiceKind.Invoice,
        };
        var match = _locator.LocateAny(ValueVariants.Kind(mapped), fuzzy: true);
        return FieldValue.Extracted(mapped, match.ToSource(), match.Confidence);
    }

    /// <summary>Free text (descriptions, notes, pack sizes): fuzzy matching tolerates OCR noise and merged words.</summary>
    private FieldValue<string> Text(string? value, int? page = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldValue<string>.Missing();
        }

        var match = _locator.LocateText(value, page);
        return FieldValue.Extracted(value, match.ToSource(), match.Confidence);
    }

    /// <summary>
    /// Identifier-like text (SKU, GTIN, lot number): exact or normalised match only, so a misread digit is NotFound
    /// instead of a confident match on a neighbouring code (docs/06 "Provenance without citations").
    /// </summary>
    private FieldValue<string> IdentifierText(string? value, int? page = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldValue<string>.Missing();
        }

        var match = _locator.LocateIdentifier(value, page);
        return FieldValue.Extracted(value, match.ToSource(), match.Confidence);
    }

    /// <summary>Document identifiers (invoice number, order and delivery note references): exact or normalised match only.</summary>
    private FieldValue<DocumentIdentifier> Identifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FieldValue<DocumentIdentifier>.Missing();
        }

        var match = _locator.LocateIdentifier(value);
        return FieldValue.Extracted(DocumentIdentifier.Create(value), match.ToSource(), match.Confidence);
    }

    private FieldValue<decimal> Number(decimal? value, int? page = null)
    {
        if (value is null)
        {
            return FieldValue<decimal>.Missing();
        }

        var match = _locator.LocateNumber(value, page);
        return FieldValue.Extracted(value.Value, match.ToSource(), match.Confidence);
    }

    private FieldValue<Money> Amount(decimal? value, int? page = null)
    {
        if (value is null)
        {
            return FieldValue<Money>.Missing();
        }

        var match = _locator.LocateNumber(value, page);
        return FieldValue.Extracted(new Money(value.Value, _currency), match.ToSource(), match.Confidence);
    }

    private FieldValue<DateOnly> Date(DateOnly? value, int? page = null)
    {
        if (value is null)
        {
            return FieldValue<DateOnly>.Missing();
        }

        var match = _locator.LocateDate(value, page);
        return FieldValue.Extracted(value.Value, match.ToSource(), match.Confidence);
    }

    private FieldValue<DatePeriod> MapPeriod(DateOnly? from, DateOnly? to)
    {
        if (from is null || to is null)
        {
            return FieldValue<DatePeriod>.Missing();
        }

        var first = _locator.LocateDate(from);
        var second = _locator.LocateDate(to);
        var weakest = ProvenanceMatch.Weaker(first, second);
        var source = first.IsFound ? first : second;
        return FieldValue.Extracted(new DatePeriod(from.Value, to.Value), source.ToSource(), weakest.Confidence);
    }

    private FieldValue<Party> MapParty(ExtractedParty? p)
    {
        if (p is null)
        {
            return FieldValue<Party>.Missing();
        }

        var hasAddress = p.AddressLine1 is not null || p.AddressLine2 is not null || p.PostalCode is not null || p.City is not null || p.CountryCode is not null;
        var party = new Party(
            Blank(p.Name),
            Blank(p.VatId),
            Blank(p.TaxId),
            Blank(p.RegistrationNumber),
            hasAddress ? new Address(Blank(p.AddressLine1), Blank(p.AddressLine2), Blank(p.PostalCode), Blank(p.City), null, Blank(p.CountryCode)) : null,
            Blank(p.Email),
            Blank(p.Phone),
            Blank(p.Iban),
            Blank(p.Bic));

        if (party.Name is null && party.VatId is null && party.TaxId is null && party.Address is null && party.Iban is null && party.Email is null)
        {
            return FieldValue<Party>.Missing();
        }

        // Provenance from the name; confidence from the weaker of name and VAT id, the two values matching relies on.
        // The name may be fuzzy (OCR noise, diacritics); the VAT id must be printed as returned.
        var name = _locator.LocateText(party.Name);
        var vat = party.VatId is null ? null : _locator.LocateIdentifier(party.VatId);
        var weakest = vat is null ? name : ProvenanceMatch.Weaker(name, vat);
        var source = name.IsFound ? name : vat ?? name;
        return FieldValue.Extracted(party, source.ToSource(), weakest.Confidence);
    }

    private TaxBreakdownLine? MapTaxRow(ExtractedTaxRow row)
    {
        // A row needs all three numbers to be a breakdown line; partial rows are not representable and TotalTax still carries the sum.
        if (row.RatePercent is null || row.TaxableAmount is null || row.TaxAmount is null)
        {
            return null;
        }

        return new TaxBreakdownLine(
            row.RatePercent.Value,
            new Money(row.TaxableAmount.Value, _currency),
            new Money(row.TaxAmount.Value, _currency),
            TaxCategoryFor(row.CategoryCode));
    }

    private ChargeOrAllowance? MapChargeOrAllowance(ExtractedChargeOrAllowance item) =>
        item.Amount is null ? null : new ChargeOrAllowance(Blank(item.Description), new Money(item.Amount.Value, _currency), item.TaxRatePercent, item.IsCharge);

    private FieldValue<PaymentTerms> MapPaymentTerms(ExtractedPaymentTerms? terms)
    {
        if (terms is null)
        {
            return FieldValue<PaymentTerms>.Missing();
        }

        var mapped = new PaymentTerms(
            terms.DueDate,
            terms.DiscountPercent,
            terms.DiscountUntil,
            PaymentMeans: null,
            Blank(terms.PaymentReference),
            Blank(terms.Iban),
            Blank(terms.Bic),
            Blank(terms.RawText));

        if (mapped is { DueDate: null, DiscountPercent: null, DiscountUntil: null, PaymentReference: null, Iban: null, Bic: null, RawText: null })
        {
            return FieldValue<PaymentTerms>.Missing();
        }

        var match = mapped.RawText is not null ? _locator.LocateText(mapped.RawText) : _locator.LocateDate(mapped.DueDate);
        return FieldValue.Extracted(mapped, match.ToSource(), match.Confidence);
    }

    private InvoiceLine MapLine(ExtractedInvoiceLine line, int index)
    {
        var page = line.Page is > 0 ? line.Page : null;
        var unitRaw = Blank(line.Unit);
        FieldValue<UnitOfMeasure> unit;
        if (unitRaw is null)
        {
            unit = FieldValue<UnitOfMeasure>.Missing();
        }
        else
        {
            var match = _locator.LocateText(unitRaw, page);
            unit = FieldValue.Extracted(UnitOfMeasureParser.Parse(unitRaw), match.ToSource(), match.Confidence);
        }

        return new InvoiceLine
        {
            LineNumber = line.LineNumber > 0 ? line.LineNumber : index + 1,
            SupplierSku = IdentifierText(line.SupplierSku, page),
            Gtin = IdentifierText(line.Gtin, page),
            Description = Text(line.Description, page),
            Quantity = Number(line.Quantity, page),
            Unit = unit,
            UnitRaw = unitRaw,
            PackSize = Text(line.PackSize, page),
            UnitPrice = Amount(line.UnitPrice, page),
            DiscountPercent = Number(line.DiscountPercent, page),
            DiscountAmount = Amount(line.DiscountAmount, page),
            LineNet = Amount(line.LineNet, page),
            TaxRate = Number(line.TaxRatePercent, page),
            LineGross = Amount(line.LineGross, page),
            DepositAmount = Amount(line.DepositAmount, page),
            LotNumber = IdentifierText(line.LotNumber, page),
            BestBefore = Date(line.BestBefore, page),
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
