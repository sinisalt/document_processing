namespace DocProc.Llm.Tests;

public class LlmOptionsTests
{
    [Fact]
    public void A_positive_or_infinite_timeout_is_accepted()
    {
        new LlmOptions { Timeout = TimeSpan.FromSeconds(30) }.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        new LlmOptions { Timeout = Timeout.InfiniteTimeSpan }.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
        LlmOptions.Default.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_zero_or_negative_timeout_is_refused_where_the_options_are_built(int seconds)
    {
        var refused = Should.Throw<ArgumentOutOfRangeException>(() => new LlmOptions { Timeout = TimeSpan.FromSeconds(seconds) });
        refused.ParamName.ShouldBe(nameof(LlmOptions.Timeout));
    }
}
