using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Text;

namespace DocProc.Domain.Tests;

public class CommonTypesTests
{
    [Theory]
    [InlineData("  re-2026/0042 ", "RE-2026/0042")]
    [InlineData("Inv   12\t34", "INV 12 34")]
    public void Document_identifiers_are_normalised_for_matching(string verbatim, string expected)
    {
        var id = DocumentIdentifier.Create(verbatim);

        id.Verbatim.ShouldBe(verbatim);
        id.Normalized.ShouldBe(expected);
        id.Matches(DocumentIdentifier.Create(expected.ToLowerInvariant())).ShouldBeTrue();
    }

    [Theory]
    [InlineData("DE 123.456-789", "DE123456789")]
    [InlineData("de123456789", "DE123456789")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Compact_normalisation_strips_separators(string? input, string? expected) =>
        Identifiers.NormalizeCompact(input).ShouldBe(expected);

    [Fact]
    public void Name_normalisation_removes_diacritics_and_punctuation()
    {
        Identifiers.NormalizeName("Gasthaus „Zur Post“ GmbH & Co. KG – München").ShouldBe("GASTHAUS ZUR POST GMBH CO KG MUNCHEN");
        Identifiers.NormalizeName("Pekara Šarić d.o.o. – Čakovec").ShouldBe("PEKARA SARIC DOO CAKOVEC");
        Identifiers.NormalizeName("Straße").ShouldBe("STRASSE");
    }

    [Theory]
    [InlineData("kg", UnitOfMeasure.Kilogram)]
    [InlineData("Stk.", UnitOfMeasure.Piece)]
    [InlineData("kom", UnitOfMeasure.Piece)]
    [InlineData("Kiste", UnitOfMeasure.Crate)]
    [InlineData("KARTON", UnitOfMeasure.Box)]
    [InlineData("ltr", UnitOfMeasure.Litre)]
    [InlineData("Std", UnitOfMeasure.Hour)]
    [InlineData("bananas", UnitOfMeasure.Other)]
    [InlineData("", UnitOfMeasure.Other)]
    [InlineData(null, UnitOfMeasure.Other)]
    public void Units_are_mapped_from_multilingual_aliases(string? raw, UnitOfMeasure expected) =>
        UnitOfMeasureParser.Parse(raw).ShouldBe(expected);

    [Fact]
    public void Money_arithmetic_requires_the_same_currency()
    {
        var a = new Money(10m, "EUR");
        (a + new Money(2.5m, "EUR")).ShouldBe(new Money(12.5m, "EUR"));
        Should.Throw<InvalidOperationException>(() => a + new Money(1m, "USD"));
        a.IsApproximately(new Money(10.02m, "EUR"), 0.02m).ShouldBeTrue();
        a.IsApproximately(new Money(10.03m, "EUR"), 0.02m).ShouldBeFalse();
        a.IsApproximately(new Money(10m, "USD"), 0.02m).ShouldBeFalse();
    }

    [Theory]
    [InlineData("EUR", true)]
    [InlineData("HRK", true)]
    [InlineData("eur", false)]
    [InlineData("EURO", false)]
    [InlineData(null, false)]
    public void Currency_codes_are_three_upper_case_letters(string? code, bool wellFormed) =>
        CurrencyCode.IsWellFormed(code).ShouldBe(wellFormed);

    [Fact]
    public void Tax_row_computes_its_expected_tax() =>
        new TaxBreakdownLine(19m, new Money(50m, "EUR"), new Money(9.5m, "EUR")).ExpectedTax.ShouldBe(new Money(9.5m, "EUR"));

    [Fact]
    public void Flag_catalogue_covers_every_code_constant()
    {
        var constants = typeof(FlagCodes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        constants.ShouldNotBeEmpty();
        constants.ShouldAllBe(c => FlagCodes.IsKnown(c));
        FlagCodes.Catalogue.Count.ShouldBe(constants.Count);
    }

    [Fact]
    public void Flags_take_default_severity_from_the_catalogue()
    {
        var flag = FlagCodes.Create(FlagCodes.TotalsMismatch, "x", "totalGross");

        flag.Severity.ShouldBe(FlagSeverity.Blocking);
        flag.IsResolved.ShouldBeFalse();
        flag.Resolve(DateTimeOffset.UnixEpoch).IsResolved.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => FlagCodes.Create("NOT_A_CODE", "x"));
    }

    [Fact]
    public void Text_pages_normalise_to_a_1000_unit_square()
    {
        var page = new TextPage(1, 595, 842, [new PositionedWord("Rechnung", new BoundingBox(59.5, 84.2, 119, 100))]);

        var normalized = page.Normalize();

        normalized.Width.ShouldBe(1000);
        normalized.Height.ShouldBe(1000);
        var box = normalized.Words[0].Box;
        box.X0.ShouldBe(100, 0.001);
        box.Y0.ShouldBe(100, 0.001);
        box.X1.ShouldBe(200, 0.001);
        new TextLayer(TextLayerSource.PdfText, [page, page]).AverageWordsPerPage.ShouldBe(1);
    }
}
