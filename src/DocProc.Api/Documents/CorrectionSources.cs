using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocProc.Domain.Fields;

namespace DocProc.Api.Documents;

/// <summary>
/// Checks and converts the optional <see cref="CorrectionSource"/> of a correction (docs/11 §12.6): the location goes
/// into the corrected field's provenance (<see cref="FieldValue{T}.Correct"/>) and, whole, into
/// <c>Correction.SourceJson</c>.
/// </summary>
public static partial class CorrectionSources
{
    /// <summary>Page units of the text layer: every page is normalised to 1000 × 1000 (docs/04).</summary>
    public const double PageUnits = 1000;

    public const int MaxMethodLength = 32;
    public const int MaxRawTextLength = 2000;
    public const int MaxWords = 500;
    public const int MaxPathLength = 400;
    public const int MaxCellLength = 32;

    /// <summary>How <c>Correction.SourceJson</c> is written: camelCase, nulls left out.</summary>
    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Throws <see cref="CanonicalPatchException"/> naming <paramref name="path"/> when the source is out of bounds.</summary>
    public static void Check(string path, CorrectionSource? source)
    {
        if (source is null)
        {
            return;
        }

        if (source.Method is { } method && !MethodPattern().IsMatch(method))
        {
            throw new CanonicalPatchException(path, $"source.method must be a short code (letters, digits and dashes, at most {MaxMethodLength} characters, starting with a letter).");
        }

        if (source.Page is < 1)
        {
            throw new CanonicalPatchException(path, "source.page is 1-based.");
        }

        if (source.BBox is { } box && !IsValid(box))
        {
            throw new CanonicalPatchException(path, $"source.bBox must lie within the page (0 to {PageUnits} on both axes) with x0 <= x1 and y0 <= y1.");
        }

        if (source.RawText is { Length: > MaxRawTextLength })
        {
            throw new CanonicalPatchException(path, $"source.rawText is longer than {MaxRawTextLength} characters.");
        }

        if (source.Words is { } words)
        {
            if (words.Count > MaxWords)
            {
                throw new CanonicalPatchException(path, $"source.words lists more than {MaxWords} words.");
            }

            if (words.Any(w => w is not { Count: 2 } || w[0] < 1 || w[1] < 0))
            {
                throw new CanonicalPatchException(path, "source.words entries are [page, wordIndex] pairs with a 1-based page and a word index from 0.");
            }
        }

        if (source.Path is { Length: > MaxPathLength })
        {
            throw new CanonicalPatchException(path, $"source.path is longer than {MaxPathLength} characters.");
        }

        if (source.Cell is { Length: > MaxCellLength })
        {
            throw new CanonicalPatchException(path, $"source.cell is longer than {MaxCellLength} characters.");
        }
    }

    /// <summary>
    /// The provenance location for the corrected field: page, box and raw text. Null when the source names none of
    /// them, so the field gets the bare <see cref="FieldSource.Human"/> exactly as a correction without a source does.
    /// </summary>
    public static FieldSource? ToFieldSource(CorrectionSource? source)
    {
        if (source is null || (source.Page is null && source.BBox is null && source.RawText is null))
        {
            return null;
        }

        BoundingBox? box = source.BBox is { } b ? new BoundingBox(b.X0, b.Y0, b.X1, b.Y1) : null;
        return new FieldSource(SourceMethod.Human, source.Page, box, source.RawText);
    }

    /// <summary>
    /// The value of <c>Correction.SourceJson</c>: a JSON object, or null when no source was given. A move records the
    /// field the value came from as <c>from</c>, next to the source the reviewer gave, if any.
    /// </summary>
    public static string? ToJson(CorrectionSource? source, string? from = null)
    {
        if (from is null)
        {
            return source is null ? null : JsonSerializer.Serialize(source, StorageOptions);
        }

        var json = source is null ? [] : (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(source, StorageOptions)!;
        json["from"] = from;
        return json.ToJsonString();
    }

    /// <summary>
    /// The <c>Correction.SourceJson</c> of a table capture (docs/11 §6.2 <c>table</c>): the source with
    /// <c>table: {spec, report}</c>, the specification the rows were read with and a copy of the report, for the audit.
    /// </summary>
    public static string ToJson(CorrectionSource? source, System.Text.Json.Nodes.JsonObject table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var json = source is null ? [] : (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(source, StorageOptions)!;
        json["table"] = table;
        return json.ToJsonString();
    }

    private static bool IsValid(SourceBox box) =>
        double.IsFinite(box.X0) && double.IsFinite(box.Y0) && double.IsFinite(box.X1) && double.IsFinite(box.Y1)
        && box.X0 >= 0 && box.Y0 >= 0 && box.X1 <= PageUnits && box.Y1 <= PageUnits
        && box.X0 <= box.X1 && box.Y0 <= box.Y1;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9-]{0,31}$")]
    private static partial Regex MethodPattern();
}
