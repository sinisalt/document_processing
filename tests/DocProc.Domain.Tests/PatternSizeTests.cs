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
}
