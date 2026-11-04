using DocProc.Domain.Validation;

namespace DocProc.Domain.Tests;

/// <summary>
/// The bound on what a tenant's or reviewer's pattern unrolls to under NonBacktracking (<see cref="PatternSize"/>): the
/// patterns of the templates and of the table tool fit easily, nested counted repetitions do not.
/// </summary>
public sealed class PatternSizeTests
{
    [Theory]
    [InlineData("^[0-9]{8}([0-9]{4,6})?$", 16)]
    [InlineData(@"^Seite\s+\d+\s+von\s+\d+", 14)]
    [InlineData(@"^(?:ab|from|von)\s*(\d(?:[\d.,\s]*\d)?)", 14)]
    [InlineData(@"^[0-9]{3}\s?[0-9]{6}$", 12)]
    [InlineData(@"\p{Lu}{2}\d{2}", 4)]
    [InlineData("[a-z-[aeiou]]{3}", 3)]
    [InlineData("a{2,}", 3)]
    [InlineData("x{", 2)]
    [InlineData("(?<year>\\d{4})-(?i:ab)", 7)]
    public void Counts_the_atoms_a_pattern_unrolls_to(string pattern, long expected)
    {
        PatternSize.Estimate(pattern).ShouldBe(expected);
        PatternSize.IsSmallEnough(pattern).ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"(?:[^§]{0,99}\d){0,99}§")]
    [InlineData("(.{0,99}){0,99}Q")]
    [InlineData("(?:(?:a{0,20}){0,20}){0,20}")]
    [InlineData("a{501}")]
    public void Refuses_counted_repetitions_that_multiply(string pattern)
    {
        PatternSize.Estimate(pattern).ShouldBeGreaterThan(PatternSize.MaxUnrolled);
        PatternSize.IsSmallEnough(pattern).ShouldBeFalse();
    }

    [Fact]
    public void A_long_literal_line_text_fits()
    {
        PatternSize.IsSmallEnough("^" + new string('x', 199)).ShouldBeTrue();
    }

    /// <summary>
    /// Patterns that unroll to fewer than <see cref="PatternSize.MaxUnrolled"/> atoms but run for seconds per match
    /// under NonBacktracking (10 to 50 s on 20,000 characters, whatever the match timeout): a repeated group whose
    /// length varies, a loop over such repetitions, alternatives of different length ranges in a long sequence, and a
    /// multi-character group repeated a varying number of times before more wildcards.
    /// </summary>
    [Theory]
    [InlineData("(.{0,15}a){30}x")]
    [InlineData("(.{0,15}e){30}q")]
    [InlineData("([abc]{0,15}a){30}x")]
    [InlineData("(.?.?.?.?.?.?.?.?.?.?.?.?.?.?.?a){30}x")]
    [InlineData("(?:a.{0,14}|b){30}x")]
    [InlineData("(?:(.{0,8}a){4}|(.{0,8}b){4}|(.{0,8}c){4})+x")]
    [InlineData("(?:a|.{0,14})(?:a|.{0,14})(?:a|.{0,14})(?:a|.{0,14})(?:a|.{0,14})(?:a|.{0,14})(?:a|.{0,14})(?:a|.{0,14})(?:a|.{0,14})(?:a|.{0,14})x")]
    [InlineData("(?:.{8}){0,55}.{0,15}a.{0,15}bx")]
    [InlineData("((.{0,3}a){5}){6}x")]
    public void Refuses_repeated_groups_whose_length_varies(string pattern)
    {
        PatternSize.Estimate(pattern).ShouldBeLessThanOrEqualTo(PatternSize.MaxUnrolled, "the unrolled count alone let it through");
        PatternSize.Variation(pattern).ShouldBeGreaterThan(PatternSize.MaxVariation);
        PatternSize.IsSmallEnough(pattern).ShouldBeFalse();
        PatternSize.Problem(pattern).ShouldNotBeNull().ShouldContain("combination of lengths");
    }

    [Theory]
    [InlineData(@"[A-Z]{2}\d{2}(?: ?[0-9A-Z]{4}){3,7}(?: ?[0-9A-Z]{1,3})?", 27)]
    [InlineData(@"[A-Z]{2}\d{2}(?:\s?[0-9A-Z]{4}){2,8}\s?[0-9A-Z]{1,4}", 38)]
    [InlineData(@"\+?\d{1,3}(?:[ -]?\d{2,4}){2,4}", 22)]
    [InlineData(@"\d{1,3}(?:[.,]\d{3}){0,5}", 20)]
    [InlineData(@"\d{1,3}(?:[ .]?\d{3})*,\d{2}", 8)]
    [InlineData(@"(?:Rechnung|Invoice|Facture)\s*(?:Nr|No|Nummer)\.?\s*(\S+)", 0)]
    [InlineData(@"^(?:ab|from|von)\s*(\d(?:[\d.,\s]*\d)?)", 0)]
    [InlineData(@"(?:\d{3}){4}", 0)]
    [InlineData(".{0,450}a", 0)]
    [InlineData(".{0,15}a.{0,15}b.{0,15}c", 0)]
    [InlineData("(?:.{0,15}a|.{0,15}b)", 0)]
    [InlineData("(?:a|.{0,14})", 14)]
    [InlineData("(.{0,16}a){4}x", 64)]
    public void Field_and_line_patterns_stay_within_the_variation(string pattern, long expected)
    {
        PatternSize.Variation(pattern).ShouldBe(expected);
        PatternSize.IsSmallEnough(pattern).ShouldBeTrue();
        PatternSize.Problem(pattern).ShouldBeNull();
    }

    [Fact]
    public void A_pattern_past_the_unrolled_count_names_that_bound()
    {
        PatternSize.Problem("a{501}").ShouldNotBeNull().ShouldContain("unrolled");
    }
}
