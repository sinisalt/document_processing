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
        var match = Locator.LocateText("METRO Deutschland GmbH");

        match.Quality.ShouldBe(MatchQuality.Exact);
        match.Box.ShouldBe(new DocProc.Domain.Fields.BoundingBox(50, 40, 290, 55));
        match.RawText.ShouldBe("METRO Deutschland GmbH");
    }

    [Fact]
    public void Normalised_match_ignores_spacing_and_case()
    {
        Locator.LocateText("DE811177936").Quality.ShouldBe(MatchQuality.Normalized);
        Locator.LocateText("metro deutschland gmbh").Quality.ShouldBe(MatchQuality.Normalized);
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
        Locator.LocateText("Métro Deutschland GmbH").Quality.ShouldBe(MatchQuality.Fuzzy);
        Locator.LocateText("METRO Deutschlan GmbH").Quality.ShouldBe(MatchQuality.Fuzzy);
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
