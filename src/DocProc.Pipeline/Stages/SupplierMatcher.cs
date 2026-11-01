using System.Text.RegularExpressions;
using DocProc.Domain.Common;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Pipeline.Stages;

/// <summary>What stage 5 learned about the document before looking at the supplier list.</summary>
/// <param name="VatIds">EU VAT ids found in the document, compact form (upper case, no separators).</param>
/// <param name="CompactText">The document text with whitespace, dots, dashes and slashes removed, for id lookups that survive odd formatting.</param>
/// <param name="SenderDomain">Domain of the sender address of the ingestion, lower case.</param>
/// <param name="NameText">The first page text (or the supplier party of an e-invoice), for the fuzzy name match.</param>
internal sealed record SupplierSignals(IReadOnlySet<string> VatIds, string CompactText, string? SenderDomain, string NameText)
{
    public static readonly SupplierSignals Empty = new(new HashSet<string>(StringComparer.Ordinal), string.Empty, null, string.Empty);
}

internal sealed record SupplierMatch(Supplier Supplier, double Confidence, string Method);

/// <summary>
/// Stage 5 (docs/03 "Match supplier"), signals in order of reliability: VAT id, IBAN, sender e-mail domain, fuzzy
/// supplier name. The layout fingerprint of docs/04 comes with the extractor-profile block. Pure: the tenant's
/// supplier list and the signals go in, the best match comes out.
/// </summary>
internal static partial class SupplierMatcher
{
    public const double VatIdConfidence = 0.98;
    public const double IbanConfidence = 0.95;
    public const double EmailDomainConfidence = 0.85;
    public const double NameOverlapThreshold = 0.8;

    private static readonly string[] CountryCodes =
    [
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "EL", "HU", "IE", "IT", "LV", "LT", "LU", "MT",
        "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE", "XI", "GB", "CHE", "NO",
    ];

    [GeneratedRegex(@"(?<![A-Z0-9])(?:AT|BE|BG|HR|CY|CZ|DK|EE|FI|FR|DE|EL|HU|IE|IT|LV|LT|LU|MT|NL|PL|PT|RO|SK|SI|ES|SE|XI|GB|CHE|NO)[ .\-]?(?:[0-9A-Z][ .\-]?){7,13}(?![A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex EuVatId();

    /// <summary>EU VAT ids as printed ("DE 123 456 789", "ATU12345678", "HR12345678901"), returned in compact form.</summary>
    public static IReadOnlySet<string> FindVatIds(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return found;
        }

        foreach (Match match in EuVatId().Matches(text.ToUpperInvariant()))
        {
            var compact = Identifiers.NormalizeCompact(match.Value);
            if (compact is { Length: >= 8 and <= 16 } && CountryCodes.Any(c => compact.StartsWith(c, StringComparison.Ordinal)))
            {
                found.Add(compact);
            }
        }

        return found;
    }

    public static SupplierMatch? Match(IReadOnlyList<Supplier> suppliers, SupplierSignals signals)
    {
        ArgumentNullException.ThrowIfNull(suppliers);
        ArgumentNullException.ThrowIfNull(signals);

        foreach (var supplier in suppliers)
        {
            if (Identifiers.NormalizeCompact(supplier.VatId) is { Length: > 0 } vatId
                && (signals.VatIds.Contains(vatId) || signals.CompactText.Contains(vatId, StringComparison.Ordinal)))
            {
                return new SupplierMatch(supplier, VatIdConfidence, "vatId");
            }
        }

        foreach (var supplier in suppliers)
        {
            foreach (var iban in supplier.Ibans.Select(Identifiers.NormalizeCompact))
            {
                if (iban is { Length: >= 15 } && signals.CompactText.Contains(iban, StringComparison.Ordinal))
                {
                    return new SupplierMatch(supplier, IbanConfidence, "iban");
                }
            }
        }

        if (signals.SenderDomain is { Length: > 0 } domain)
        {
            foreach (var supplier in suppliers)
            {
                if (supplier.EmailDomains.Any(d => DomainMatches(domain, d)))
                {
                    return new SupplierMatch(supplier, EmailDomainConfidence, "emailDomain");
                }
            }
        }

        var textTokens = Tokens(Identifiers.NormalizeName(signals.NameText));
        if (textTokens.Count == 0)
        {
            return null;
        }

        SupplierMatch? best = null;
        var bestOverlap = 0d;
        foreach (var supplier in suppliers)
        {
            foreach (var name in Names(supplier))
            {
                var nameTokens = Tokens(name);
                if (nameTokens.Count == 0)
                {
                    continue;
                }

                var overlap = (double)nameTokens.Count(textTokens.Contains) / nameTokens.Count;
                if (overlap >= NameOverlapThreshold && overlap > bestOverlap)
                {
                    // 0.8 overlap → 0.76, full overlap → 0.8: below the id-based signals, so a name-only match stays visible as such.
                    bestOverlap = overlap;
                    best = new SupplierMatch(supplier, Math.Round(0.6 + (0.2 * overlap), 3), "name");
                }
            }
        }

        return best;
    }

    public static string? DomainOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1)
        {
            return null;
        }

        var domain = address[(at + 1)..].Trim().TrimEnd('>').Trim();
        return domain.Length == 0 ? null : domain.ToLowerInvariant();
    }

    private static bool DomainMatches(string senderDomain, string supplierDomain)
    {
        var candidate = supplierDomain.Trim().ToLowerInvariant();
        return candidate.Length > 0
               && (senderDomain.Equals(candidate, StringComparison.Ordinal) || senderDomain.EndsWith("." + candidate, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Names(Supplier supplier)
    {
        yield return supplier.NormalizedName;
        foreach (var alias in supplier.Aliases)
        {
            yield return Identifiers.NormalizeName(alias);
        }
    }

    private static HashSet<string> Tokens(string normalizedName) =>
        normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
}
