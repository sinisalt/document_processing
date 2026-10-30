using System.Text.RegularExpressions;

namespace DocProc.Infrastructure.Classification;

/// <summary>
/// Heuristic language detection by keyword hits (docs/03 stage 4): every matched rule votes for its language, and a
/// short list of invoice vocabulary per language ("Betrag", "amount", "iznos", "importo", "montant") breaks ties.
/// Returns an ISO 639-1 code from <see cref="DocumentLanguages"/>, or null when nothing matched.
/// </summary>
internal static class LanguageDetector
{
    private const double RuleHitWeight = 3;
    private const int MaxHitsPerVocabularyWord = 3;
    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly IReadOnlyDictionary<string, Regex> Vocabulary = new Dictionary<string, Regex>(StringComparer.Ordinal)
    {
        [DocumentLanguages.German] = Compile("und", "der", "die", "das", "Betrag", "Datum", "MwSt", "USt", "Summe", "Gesamtbetrag", "Menge", "Netto", "Brutto", "zahlbar", "Zahlung", "Kunde", "Lieferant", "Fälligkeit", "Steuernummer"),
        [DocumentLanguages.English] = Compile("the", "and", "total", "amount", "VAT", "tax", "due", "payment", "quantity", "description", "subtotal", "customer", "supplier", "terms"),
        [DocumentLanguages.Croatian] = Compile("za", "ukupno", "iznos", "PDV", "datum", "količina", "cijena", "plaćanje", "dospijeće", "kupac", "dobavljač", "OIB"),
        [DocumentLanguages.Italian] = Compile("per", "totale", "importo", "IVA", "quantità", "prezzo", "pagamento", "scadenza", "cliente", "fornitore", "partita"),
        [DocumentLanguages.French] = Compile("et", "pour", "montant", "TVA", "quantité", "prix", "paiement", "échéance", "client", "fournisseur", "siret"),
    };

    public static string? Detect(IReadOnlyList<IndexedPageText> pages, IReadOnlyList<KeywordHit> hits)
    {
        var scores = DocumentLanguages.All.ToDictionary(l => l, _ => 0d, StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            scores[hit.Rule.Language] += RuleHitWeight;
        }

        foreach (var page in pages)
        {
            foreach (var (language, regex) in Vocabulary)
            {
                var perWord = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in regex.Matches(page.Text))
                {
                    var count = perWord.GetValueOrDefault(match.Value);
                    if (count < MaxHitsPerVocabularyWord)
                    {
                        perWord[match.Value] = count + 1;
                        scores[language] += 1;
                    }
                }
            }
        }

        var best = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).First();
        return best.Value > 0 ? best.Key : null;
    }

    private static Regex Compile(params string[] words) =>
        new($@"(?<!\p{{L}})(?:{string.Join('|', words.Select(Regex.Escape))})(?!\p{{L}})", Flags);
}
