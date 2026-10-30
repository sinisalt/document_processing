namespace DocProc.Domain.Fields;

/// <summary>Non-generic view over <see cref="FieldValue{T}"/> so validation can walk a document without knowing field types.</summary>
public interface IFieldValue
{
    object? BoxedValue { get; }
    FieldStatus Status { get; }
    double? Confidence { get; }
    FieldSource? Source { get; }

    /// <summary>True when a usable value is present (status is not Missing or Unreadable and the value is not null).</summary>
    bool HasValue { get; }
}

/// <summary>One entry in the correction trail of a field.</summary>
public sealed record FieldHistoryEntry<T>(T? Value, FieldStatus Status, FieldSource? Source, double? Confidence, DateTimeOffset At, string By);

/// <summary>
/// An extracted value together with its provenance. Every field of the canonical model is a <see cref="FieldValue{T}"/>,
/// never a bare value, so downstream code can tell "0.00 was printed" from "nothing was found".
/// </summary>
public sealed record FieldValue<T> : IFieldValue
{
    private readonly double? _confidence;

    public T? Value { get; init; }

    public FieldStatus Status { get; init; } = FieldStatus.Missing;

    /// <summary>Calibrated confidence in [0, 1], or null when the source gives none. See docs/02-data-model.md §3.</summary>
    public double? Confidence
    {
        get => _confidence;
        init
        {
            if (value is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(Confidence), value, "Confidence must be between 0 and 1.");
            }

            _confidence = value;
        }
    }

    public FieldSource? Source { get; init; }

    public IReadOnlyList<FieldHistoryEntry<T>> History { get; init; } = [];

    object? IFieldValue.BoxedValue => Value;

    public bool HasValue => Value is not null && Status is not (FieldStatus.Missing or FieldStatus.Unreadable);

    public static FieldValue<T> Missing() => new();

    public static FieldValue<T> Unreadable(FieldSource? source = null) =>
        new() { Status = FieldStatus.Unreadable, Source = source, Confidence = 0 };

    public static FieldValue<T> Extracted(T value, FieldSource? source = null, double? confidence = null) =>
        new() { Value = value, Status = FieldStatus.Extracted, Source = source, Confidence = confidence };

    public static FieldValue<T> Inferred(T value, FieldSource? source = null, double? confidence = null) =>
        new() { Value = value, Status = FieldStatus.Inferred, Source = source, Confidence = confidence };

    public static FieldValue<T> Conflict(T? preferred, FieldSource? source = null) =>
        new() { Value = preferred, Status = FieldStatus.Conflict, Source = source, Confidence = 0 };

    /// <summary>Human correction: pushes the current state to the history and returns the corrected field with confidence 1.</summary>
    public FieldValue<T> Correct(T? newValue, string by, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        var history = new List<FieldHistoryEntry<T>>(History) { new(Value, Status, Source, Confidence, at, by) };
        return new FieldValue<T>
        {
            Value = newValue,
            Status = FieldStatus.Corrected,
            Confidence = 1,
            Source = FieldSource.Human,
            History = history,
        };
    }

    public FieldValue<T> WithConfidence(double? confidence) => this with { Confidence = confidence };

    public override string ToString() => HasValue ? $"{Value} ({Status})" : Status.ToString();
}

/// <summary>Type-inferring factories: <c>FieldValue.Extracted(12.5m)</c> instead of <c>FieldValue&lt;decimal&gt;.Extracted(12.5m)</c>.</summary>
public static class FieldValue
{
    public static FieldValue<T> Extracted<T>(T value, FieldSource? source = null, double? confidence = null) =>
        FieldValue<T>.Extracted(value, source, confidence);

    public static FieldValue<T> Inferred<T>(T value, FieldSource? source = null, double? confidence = null) =>
        FieldValue<T>.Inferred(value, source, confidence);

    public static FieldValue<T> Missing<T>() => FieldValue<T>.Missing();

    public static FieldValue<T> Unreadable<T>(FieldSource? source = null) => FieldValue<T>.Unreadable(source);
}
