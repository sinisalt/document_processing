using DocProc.Api.Documents;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Values;

namespace DocProc.Api.Tests;

/// <summary>The table tool's request rules that need no document (docs/11 §12.4): the default tier header pattern.</summary>
public sealed class TableCaptureTests
{
    [Theory]
    [InlineData("ab 10", "de-DE", 10)]
    [InlineData("Ab 50", "de-DE", 50)]
    [InlineData("ab 1.000", "de-DE", 1000)]
    [InlineData("ab 1 000", "de-DE", 1000)]
    [InlineData("ab 1.000 Stück", "de-DE", 1000)]
    [InlineData("von 2.500", "de-DE", 2500)]
    [InlineData("ab 2,5 kg", "de-DE", 2.5)]
    [InlineData("from 1,000", "en-GB", 1000)]
    [InlineData("from 12", "en-US", 12)]
    public void The_default_tier_pattern_reads_the_whole_minimum_quantity_in_the_locale(string header, string locale, double expected)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var pattern = TableCapture.TierPattern(null, errors);
        errors.ShouldBeEmpty();
        ValueLocale.TryParse(locale, out var parsed).ShouldBeTrue();

        ColumnTableEngine.TierQuantity(pattern, header, parsed).ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("Preis")]
    [InlineData("Staffel")]
    [InlineData("10 Stück")]
    public void A_header_that_names_no_tier_is_no_tier(string header)
    {
        ColumnTableEngine.TierQuantity(TableCapture.TierPattern(null, new Dictionary<string, string[]>(StringComparer.Ordinal)), header, ValueLocale.German).ShouldBeNull();
    }
}
