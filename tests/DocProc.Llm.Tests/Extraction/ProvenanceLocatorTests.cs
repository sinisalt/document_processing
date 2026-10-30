using DocProc.Domain.Invoices;
using DocProc.Llm.Extraction;

namespace DocProc.Llm.Tests.Extraction;

public class ProvenanceLocatorTests
{
    private static readonly ProvenanceLocator Locator = new(Inputs.SampleTextLayer());

    [Fact]
    public void Exact_match_returns_page_box_and_raw_text()
    {
        var match = Locator.LocateText("RE-2026-0042");

        match.Quality.ShouldBe(MatchQuality.Exact);
        match.Page.ShouldBe(1);
        match.Box.ShouldBe(new DocProc.Domain.Fields.BoundingBox(180, 100, 300, 115));
        match.RawText.ShouldBe("RE-2026-0042");
        match.Confidence.ShouldBe(LocatabilityConfidence.Exact);
    }

    [Fact]
    public void Multi_word_exact_match_unions_the_word_boxes()
    {
        var match = Locator.LocateText("Musterhandel GmbH");

        match.Quality.ShouldBe(MatchQuality.Exact);
        match.Box.ShouldBe(new DocProc.Domain.Fields.BoundingBox(50, 40, 230, 55));
        match.RawText.ShouldBe("Musterhandel GmbH");
    }

    [Fact]
    public void Normalised_match_ignores_spacing_and_case()
    {
        Locator.LocateText("DE000000000").Quality.ShouldBe(MatchQuality.Normalized);
        Locator.LocateText("musterhandel gmbh").Quality.ShouldBe(MatchQuality.Normalized);
    }

    [Fact]
    public void Numbers_match_their_printed_format_and_dates_their_printed_layout()
    {
        Locator.LocateNumber(166.50m).ShouldSatisfyAllConditions(
            m => m.Quality.ShouldBe(MatchQuality.Exact),
            m => m.RawText.ShouldBe("166,50"),
            m => m.Box.ShouldBe(new DocProc.Domain.Fields.BoundingBox(770, 900, 830, 915)));
        Locator.LocateNumber(10m).RawText.ShouldBe("10,00");
        Locator.LocateDate(new DateOnly(2026, 10, 27)).ShouldSatisfyAllConditions(
            m => m.Quality.ShouldBe(MatchQuality.Exact),
            m => m.RawText.ShouldBe("27.10.2026"));
        Locator.LocateDate(new DateOnly(2026, 11, 10)).Quality.ShouldBe(MatchQuality.NotFound);
        Locator.LocateNumber(999.99m).Quality.ShouldBe(MatchQuality.NotFound);
    }

    [Fact]
    public void Fuzzy_match_covers_containment_diacritics_and_small_edits()
    {
        Locator.LocateText("Tomaten").ShouldSatisfyAllConditions(
            m => m.Quality.ShouldBe(MatchQuality.Fuzzy),
            m => m.RawText.ShouldBe("Strauchtomaten"));
        Locator.LocateText("Músterhandel GmbH").Quality.ShouldBe(MatchQuality.Fuzzy);
        Locator.LocateText("Musterhandl GmbH").Quality.ShouldBe(MatchQuality.Fuzzy);
        Locator.LocateText("Musterhandel Gmbh & Co").Quality.ShouldBe(MatchQuality.Fuzzy);
        Locator.LocateText("Completely different words").Quality.ShouldBe(MatchQuality.NotFound);
    }

    [Fact]
    public void Preferred_page_is_searched_first()
    {
        var layer = new TextLayerBuilder().Page(1).Line("Total", "12,00").Page(2).Line("Total", "12,00").Build();
        var locator = new ProvenanceLocator(layer);

        locator.LocateNumber(12m).Page.ShouldBe(1);
        locator.LocateNumber(12m, preferredPage: 2).Page.ShouldBe(2);
    }

    [Fact]
    public void Without_a_text_layer_nothing_is_found()
    {
        ProvenanceLocator.Empty.HasText.ShouldBeFalse();
        ProvenanceLocator.Empty.LocateText("anything").ShouldBe(ProvenanceMatch.None);
        ProvenanceLocator.Empty.LocateText("anything").Confidence.ShouldBe(LocatabilityConfidence.NotFound);
    }

    [Fact]
    public void Kind_and_currency_variants_locate_title_words_and_symbols()
    {
        Locator.LocateAny(ValueVariants.Kind(InvoiceKind.Invoice), fuzzy: true).RawText.ShouldBe("Rechnung");
        Locator.LocateAny(ValueVariants.Currency("EUR")).RawText.ShouldBe("EUR");
        ValueVariants.Currency("EUR").ShouldContain("€");
        ValueVariants.Kind(InvoiceKind.CreditNote).ShouldContain("Gutschrift");
    }

    [Fact]
    public void Number_variants_cover_european_and_english_formats()
    {
        var variants = ValueVariants.Number(1234.5m);

        variants.ShouldContain("1234.5");
        variants.ShouldContain("1234,50");
        variants.ShouldContain("1.234,50");
        variants.ShouldContain("1,234.50");
        variants.ShouldContain("1 234,50");
        variants.ShouldNotContain("1,234,50");

        ValueVariants.Number(-7m).ShouldContain("-7,00");
        ValueVariants.Number(7m).ShouldContain("7");
    }

    [Fact]
    public void Negative_numbers_cover_trailing_minus_parentheses_and_the_unicode_minus()
    {
        var variants = ValueVariants.Number(-12m);

        variants.ShouldContain("-12,00");
        variants.ShouldContain("12,00-");
        variants.ShouldContain("(12,00)");
        variants.ShouldContain("\u221212,00");
        variants.ShouldContain("12.00-");
        ValueVariants.Number(12m).ShouldNotContain("12,00-");

        var creditNote = new ProvenanceLocator(new TextLayerBuilder().Page(1).Line("Gutschrift", "12,00-").Line("Skonto", "(3,50)").Line("Storno", "\u22127,25").Build());
        creditNote.LocateNumber(-12m).ShouldSatisfyAllConditions(m => m.Quality.ShouldBe(MatchQuality.Exact), m => m.RawText.ShouldBe("12,00-"));
        creditNote.LocateNumber(-3.5m).RawText.ShouldBe("(3,50)");
        creditNote.LocateNumber(-7.25m).RawText.ShouldBe("\u22127,25");
    }

    [Fact]
    public void Negative_amounts_prefer_the_signed_token_and_fall_back_to_the_unsigned_one()
    {
        // A credit note quotes the original (positive) invoice amount before the credited one, and the text layer often
        // carries the minus as a word of its own, so only the normalised pass can find the credited amount. Edge
        // punctuation is stripped in that pass, so without the sign check the earlier positive token would win.
        var creditNote = new ProvenanceLocator(new TextLayerBuilder()
            .Page(1)
            .Line("Gutschrift", "zu", "Rechnung", "RE-2026-0042")
            .Line("Rechnungsbetrag", "120,00")
            .Line("Gutschrift", "-", "120,00")
            .Line("Storno", "45,50", "-")
            .Line("Rabatt", "(", "3,00", ")")
            .Build());

        creditNote.LocateNumber(-120m).ShouldSatisfyAllConditions(
            m => m.Quality.ShouldBe(MatchQuality.Normalized),
            m => m.RawText.ShouldBe("- 120,00"),
            m => m.Box!.Value.Y0.ShouldBe(130, "the third line, not the positive amount on the second"));
        creditNote.LocateNumber(120m).ShouldSatisfyAllConditions(
            m => m.Quality.ShouldBe(MatchQuality.Exact),
            m => m.RawText.ShouldBe("120,00"),
            m => m.Box!.Value.Y0.ShouldBe(100, "the positive amount keeps matching the unsigned token"));
        creditNote.LocateNumber(-45.5m).ShouldSatisfyAllConditions(
            m => m.Quality.ShouldBe(MatchQuality.Normalized),
            m => m.RawText.ShouldBe("45,50 -"));
        creditNote.LocateNumber(-3m).ShouldSatisfyAllConditions(
            m => m.Quality.ShouldBe(MatchQuality.Normalized),
            m => m.RawText.ShouldBe("( 3,00 )"));

        // Printed without any sign (the document kind carries it): the unsigned token counts, but never as an exact match.
        var unsignedOnly = new ProvenanceLocator(new TextLayerBuilder().Page(1).Line("Gutschrift", "Betrag", "80,00").Build());
        unsignedOnly.LocateNumber(-80m).ShouldSatisfyAllConditions(
            m => m.Quality.ShouldBe(MatchQuality.Normalized),
            m => m.RawText.ShouldBe("80,00"));
        unsignedOnly.LocateNumber(80m).Quality.ShouldBe(MatchQuality.Exact);
        unsignedOnly.LocateNumber(-81m).Quality.ShouldBe(MatchQuality.NotFound);
    }

    [Fact]
    public void A_sign_word_counts_only_on_the_amounts_own_line_and_never_inside_a_range()
    {
        // A dash bullet starting the next line is not the sign of the amount that ends the line above it.
        var bulleted = new ProvenanceLocator(new TextLayerBuilder()
            .Page(1)
            .Line("Rechnungsbetrag", "120,00")
            .Line("-", "Lieferung", "vom", "01.10.")
            .Line("Gutschrift", "-", "120,00")
            .Build());
        bulleted.LocateNumber(-120m).ShouldSatisfyAllConditions(
            m => m.RawText.ShouldBe("- 120,00"),
            m => (m.Box!.Value.Y1 - m.Box!.Value.Y0).ShouldBeLessThan(30, "the box stays on one line"),
            m => m.Box!.Value.Y0.ShouldBeGreaterThan(110, "the third line, not the positive amount on the first"));

        // A dash at the end of a line is not the sign of the amount that starts the next one.
        var trailingDash = new ProvenanceLocator(new TextLayerBuilder()
            .Page(1)
            .Line("Zwischensumme", "-")
            .Line("120,00", "Euro")
            .Build());
        trailingDash.LocateNumber(-120m).ShouldSatisfyAllConditions(
            m => m.RawText.ShouldBe("120,00", "the unsigned fallback, not a window reaching back to the line above"),
            m => m.Quality.ShouldBe(MatchQuality.Normalized));

        // "100,00 - 120,00" is a range: the dash between two numbers is not the first amount's trailing minus.
        var range = new ProvenanceLocator(new TextLayerBuilder()
            .Page(1)
            .Line("Spanne", "100,00", "-", "120,00")
            .Line("Gutschrift", "100,00", "-")
            .Build());
        range.LocateNumber(-100m).ShouldSatisfyAllConditions(
            m => m.RawText.ShouldBe("100,00 -"),
            m => m.Box!.Value.Y0.ShouldBeGreaterThan(range.LocateNumber(120m).Box!.Value.Y0, "the second line, where the dash is a sign"));
    }

    [Fact]
    public void Identifiers_match_exactly_or_normalised_but_never_fuzzily()
    {
        Locator.LocateIdentifier("RE-2026-0042").Quality.ShouldBe(MatchQuality.Exact);
        Locator.LocateIdentifier("re-2026-0042").Quality.ShouldBe(MatchQuality.Normalized);
        Locator.LocateIdentifier("DE 000000000").Quality.ShouldBe(MatchQuality.Exact, "printed as two words");
        Locator.LocateIdentifier("DE000000000").Quality.ShouldBe(MatchQuality.Normalized);
        Locator.LocateIdentifier("PO-77").Quality.ShouldBe(MatchQuality.Exact);

        // One character off is a different identifier: not found, never a confident match on the printed neighbour.
        Locator.LocateIdentifier("RE-2026-0043").ShouldBe(ProvenanceMatch.None);
        Locator.LocateIdentifier("DE000000001").ShouldBe(ProvenanceMatch.None);
        Locator.LocateIdentifier("PO-78").ShouldBe(ProvenanceMatch.None);
        Locator.LocateIdentifier("  ").ShouldBe(ProvenanceMatch.None);

        // The same probe through the free-text path still matches fuzzily, which is exactly what identifiers must not do.
        Locator.LocateText("RE-2026-0043").Quality.ShouldBe(MatchQuality.Fuzzy);
    }

    [Fact]
    public void Fuzzy_windows_are_anchored_on_the_first_or_last_token()
    {
        var layer = new TextLayerBuilder().Page(1).Line("Bio", "Strauchtomaten", "Klasse", "I", "lose").Build();
        var locator = new ProvenanceLocator(layer);

        // First token one edit off, rest exact: anchored at the start.
        locator.LocateText("Bioo Strauchtomaten Klasse").Quality.ShouldBe(MatchQuality.Fuzzy);
        // Last token exact, first token garbled: anchored at the end.
        locator.LocateText("Strauchtomatn Klasse I lose").Quality.ShouldBe(MatchQuality.Fuzzy);
        // Both ends off by more than one edit: not a candidate however close the middle is.
        locator.LocateText("XXXX Strauchtomaten Klasse YYYY").Quality.ShouldBe(MatchQuality.NotFound);

        ProvenanceLocator.WithinOneEdit("tomaten", "tomaten").ShouldBeTrue();
        ProvenanceLocator.WithinOneEdit("tomaten", "tomatn").ShouldBeTrue();
        ProvenanceLocator.WithinOneEdit("tomaten", "tomatten").ShouldBeTrue();
        ProvenanceLocator.WithinOneEdit("tomaten", "tomatan").ShouldBeTrue();
        ProvenanceLocator.WithinOneEdit("tomaten", "tomtn").ShouldBeFalse();
        ProvenanceLocator.WithinOneEdit("tomaten", "tamatan").ShouldBeFalse();
    }

    [Fact]
    public void Two_hundred_fuzzy_misses_on_a_ten_thousand_word_layer_finish_within_ten_seconds()
    {
        // 20 pages × 50 lines × 10 words; every page shares vocabulary with the queries so anchors do fire.
        var builder = new TextLayerBuilder();
        var word = 0;
        for (var page = 1; page <= 20; page++)
        {
            builder.Page(page);
            for (var line = 0; line < 50; line++)
            {
                builder.Line(Enumerable.Range(0, 10).Select(_ => $"Artikel{word++ % 977}x{word % 13}").ToArray());
            }
        }

        var layer = builder.Build();
        layer.WordCount.ShouldBe(10_000);
        var locator = new ProvenanceLocator(layer);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            var query = i % 2 == 0
                ? $"Artikel{i}x{i % 13} Kartoffeln festkochend 25 kg Sack Nr {i * 7}"
                : $"Lieferung Kartoffeln festkochend {i} kg Sack Artikel{i + 5}x{(i + 5) % 13}";
            locator.LocateText(query, preferredPage: (i % 20) + 1).Quality.ShouldBe(MatchQuality.NotFound);
        }

        stopwatch.Stop();
        // Alone this takes well under a second; the bound leaves room for a busy shared runner while still catching
        // a quadratic regression, which takes minutes on this layer.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), $"200 misses took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Date_variants_cover_numeric_and_month_name_layouts()
    {
        var variants = ValueVariants.Date(new DateOnly(2026, 3, 5));

        variants.ShouldContain("2026-03-05");
        variants.ShouldContain("05.03.2026");
        variants.ShouldContain("5.3.2026");
        variants.ShouldContain("05/03/2026");
        variants.ShouldContain("03/05/2026");
        variants.ShouldContain("5 March 2026");
        variants.ShouldContain("5. März 2026");
        variants.ShouldContain("5. ožujka 2026.");
    }
}
