using DocProc.Domain.Fields;

namespace DocProc.Domain.Tests;

public class FieldValueTests
{
    [Fact]
    public void Missing_and_unreadable_have_no_value()
    {
        FieldValue<string>.Missing().HasValue.ShouldBeFalse();
        FieldValue<decimal>.Unreadable().HasValue.ShouldBeFalse();
        FieldValue<decimal>.Unreadable().Confidence.ShouldBe(0);
    }

    [Fact]
    public void Extracted_default_struct_value_still_counts_as_present()
    {
        // 0.00 printed on the document is a value, not an absence.
        FieldValue.Extracted(0m).HasValue.ShouldBeTrue();
    }

    [Fact]
    public void Confidence_outside_unit_interval_is_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => FieldValue.Extracted("x", confidence: 1.5));
        Should.Throw<ArgumentOutOfRangeException>(() => FieldValue.Extracted("x", confidence: -0.1));
    }

    [Fact]
    public void Correct_keeps_the_previous_state_in_history()
    {
        var original = FieldValue.Extracted("RE-1", new FieldSource(SourceMethod.Llm, 1), 0.7);
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        var corrected = original.Correct("RE-10", "reviewer@example.com", at);

        corrected.Value.ShouldBe("RE-10");
        corrected.Status.ShouldBe(FieldStatus.Corrected);
        corrected.Confidence.ShouldBe(1);
        corrected.Source!.Method.ShouldBe(SourceMethod.Human);
        var entry = corrected.History.ShouldHaveSingleItem();
        entry.Value.ShouldBe("RE-1");
        entry.Status.ShouldBe(FieldStatus.Extracted);
        entry.Confidence.ShouldBe(0.7);
        entry.By.ShouldBe("reviewer@example.com");
        entry.At.ShouldBe(at);
    }

    [Fact]
    public void Second_correction_appends_to_history()
    {
        var at = DateTimeOffset.UnixEpoch;
        var twice = FieldValue.Extracted(1m).Correct(2m, "a", at).Correct(3m, "b", at.AddMinutes(1));

        twice.History.Count.ShouldBe(2);
        twice.History[1].Value.ShouldBe(2m);
        twice.History[1].Status.ShouldBe(FieldStatus.Corrected);
    }
}
