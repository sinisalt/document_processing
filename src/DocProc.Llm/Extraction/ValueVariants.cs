using System.Globalization;
using DocProc.Domain.Invoices;

namespace DocProc.Llm.Extraction;

/// <summary>
/// The printed spellings a typed value may have had: the model returns numbers and dates in canonical JSON form,
/// so locating them in the text layer means trying the European and English formats they were printed in.
/// </summary>
public static class ValueVariants
{
    private static readonly string[] ThousandsSeparators = ["", ".", ",", " ", "'", " "];
    private static readonly string[] DecimalSeparators = [".", ","];

    private static readonly string[] MonthsEnglish =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private static readonly string[] MonthsGerman =
        ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];

    private static readonly string[] MonthsCroatian =
        ["siječnja", "veljače", "ožujka", "travnja", "svibnja", "lipnja", "srpnja", "kolovoza", "rujna", "listopada", "studenoga", "prosinca"];

    private static readonly Dictionary<string, string[]> CurrencySymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EUR"] = ["€", "Euro", "EURO"],
        ["USD"] = ["$", "US$"],
        ["GBP"] = ["£"],
        ["CHF"] = ["Fr.", "SFr."],
        ["PLN"] = ["zł"],
        ["CZK"] = ["Kč"],
        ["HUF"] = ["Ft"],
        ["DKK"] = ["kr", "kr."],
        ["SEK"] = ["kr", "kr."],
        ["NOK"] = ["kr", "kr."],
    };

    private static readonly Dictionary<InvoiceKind, string[]> KindKeywords = new()
    {
        [InvoiceKind.Invoice] = ["Invoice", "INVOICE", "Rechnung", "RECHNUNG", "Račun", "RAČUN", "Faktura", "FAKTURA", "Fattura", "Facture", "Factura", "Tax Invoice"],
        [InvoiceKind.CreditNote] = ["Credit Note", "CREDIT NOTE", "Credit note", "Gutschrift", "GUTSCHRIFT", "Odobrenje", "ODOBRENJE", "Avoir", "Nota di credito", "Storno", "Stornorechnung"],
        [InvoiceKind.Proforma] = ["Proforma", "PROFORMA", "Pro forma", "Pro-forma", "Proforma Invoice", "Predračun", "Proformarechnung"],
    };

    /// <summary>All plausible printed forms of a decimal: printed scale, two decimals and (for integers) no decimals, with every separator convention.</summary>
    public static IReadOnlyList<string> Number(decimal value)
    {
        var abs = Math.Abs(value);
        var scale = abs.Scale;
        var decimals = new HashSet<int> { scale, 2 };
        if (abs == decimal.Truncate(abs))
        {
            decimals.Add(0);
        }

        var result = new List<string>();
        var sign = value < 0 ? "-" : string.Empty;
        foreach (var d in decimals.OrderBy(x => x))
        {
            var fixedPoint = abs.ToString("F" + d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            var dot = fixedPoint.IndexOf('.', StringComparison.Ordinal);
            var integerPart = dot < 0 ? fixedPoint : fixedPoint[..dot];
            var fraction = dot < 0 ? string.Empty : fixedPoint[(dot + 1)..];

            foreach (var thousands in ThousandsSeparators)
            {
                if (thousands.Length > 0 && integerPart.Length <= 3)
                {
                    continue;
                }

                var grouped = Group(integerPart, thousands);
                if (fraction.Length == 0)
                {
                    result.Add(sign + grouped);
                    continue;
                }

                foreach (var dec in DecimalSeparators)
                {
                    if (dec == thousands)
                    {
                        continue;
                    }

                    result.Add(sign + grouped + dec + fraction);
                }
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Numeric and month-name date layouts used across the EU and in English documents.</summary>
    public static IReadOnlyList<string> Date(DateOnly value)
    {
        var y = value.Year.ToString("0000", CultureInfo.InvariantCulture);
        var yy = (value.Year % 100).ToString("00", CultureInfo.InvariantCulture);
        var mm = value.Month.ToString("00", CultureInfo.InvariantCulture);
        var m = value.Month.ToString(CultureInfo.InvariantCulture);
        var dd = value.Day.ToString("00", CultureInfo.InvariantCulture);
        var d = value.Day.ToString(CultureInfo.InvariantCulture);

        var variants = new List<string>
        {
            $"{y}-{mm}-{dd}",
            $"{dd}.{mm}.{y}", $"{d}.{m}.{y}", $"{dd}.{mm}.{yy}", $"{d}.{m}.{yy}", $"{dd}.{mm}.{y}.", $"{d}.{m}.{y}.",
            $"{dd}/{mm}/{y}", $"{d}/{m}/{y}", $"{dd}/{mm}/{yy}", $"{mm}/{dd}/{y}", $"{m}/{d}/{y}",
            $"{dd}-{mm}-{y}", $"{d}-{m}-{y}", $"{y}/{mm}/{dd}", $"{y}.{mm}.{dd}", $"{dd} {mm} {y}",
            $"{d} {MonthsEnglish[value.Month - 1]} {y}", $"{MonthsEnglish[value.Month - 1]} {d}, {y}", $"{d}{Ordinal(value.Day)} {MonthsEnglish[value.Month - 1]} {y}",
            $"{d}. {MonthsGerman[value.Month - 1]} {y}", $"{dd}. {MonthsGerman[value.Month - 1]} {y}",
            $"{d}. {MonthsCroatian[value.Month - 1]} {y}.", $"{dd}. {MonthsCroatian[value.Month - 1]} {y}.",
        };

        return variants.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The ISO code plus the symbols and words a document prints for it.</summary>
    public static IReadOnlyList<string> Currency(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var variants = new List<string> { code };
        if (CurrencySymbols.TryGetValue(code, out var symbols))
        {
            variants.AddRange(symbols);
        }

        return variants;
    }

    /// <summary>Title words that identify an invoice, credit note or pro-forma in the languages the pipeline sees.</summary>
    public static IReadOnlyList<string> Kind(InvoiceKind kind) => KindKeywords.TryGetValue(kind, out var words) ? words : [];

    private static string Group(string digits, string separator)
    {
        if (separator.Length == 0 || digits.Length <= 3)
        {
            return digits;
        }

        var parts = new List<string>();
        for (var end = digits.Length; end > 0; end -= 3)
        {
            var start = Math.Max(0, end - 3);
            parts.Insert(0, digits[start..end]);
        }

        return string.Join(separator, parts);
    }

    private static string Ordinal(int day) => day switch
    {
        1 or 21 or 31 => "st",
        2 or 22 => "nd",
        3 or 23 => "rd",
        _ => "th",
    };
}
