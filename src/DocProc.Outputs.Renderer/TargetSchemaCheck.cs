using System.Globalization;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace DocProc.Outputs.Renderer;

/// <summary>
/// The target-schema check of docs/11 §13.2: a render's output against the receiver's JSON Schema
/// (<c>OutputMappingVersion.TargetSchemaJson</c>), with JsonSchema.Net. It runs here, in the renderer process, because
/// the schema is the tenant's: its <c>pattern</c> keywords run a backtracking regex without a timeout, which only the
/// host's time limit on this process bounds. References resolve within the schema only (no registry, no fetching).
/// </summary>
public static class TargetSchemaCheck
{
    /// <summary>At most this many schema errors are reported.</summary>
    public const int MaxErrors = 100;

    public sealed record SchemaError(string Path, string Location, string Keyword, string Message);

    /// <summary>The schema built from its JSON, or the reason it is not a schema.</summary>
    public static JsonSchema Build(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The target schema is a JSON object.");
        }

        return JsonSchema.Build(document.RootElement.Clone(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
    }

    /// <summary>The errors of <paramref name="outputJson"/> (none when it fits), at most <see cref="MaxErrors"/>.</summary>
    public static IReadOnlyList<SchemaError> Check(JsonSchema schema, string outputJson)
    {
        using var document = JsonDocument.Parse(outputJson);
        EvaluationResults results;
        try
        {
            results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        }
        catch (Exception e) when (e is JsonSchemaException or RefResolutionException or InvalidOperationException or ArgumentException)
        {
            // A reference that resolves nowhere (no registry, no fetching) surfaces only when the evaluation reaches it.
            return [new SchemaError(string.Empty, string.Empty, "schema", "The target schema cannot be evaluated: " + e.Message)];
        }

        if (results.IsValid)
        {
            return [];
        }

        var errors = new List<SchemaError>();
        var seen = new HashSet<(string, string)>();
        foreach (var detail in (results.Details ?? []).Where(d => d.Errors is { Count: > 0 }))
        {
            var pointer = detail.InstanceLocation.ToString();
            foreach (var (keyword, message) in detail.Errors!)
            {
                if (errors.Count >= MaxErrors)
                {
                    return errors;
                }

                if (seen.Add((pointer, keyword)))
                {
                    errors.Add(new SchemaError(ToPath(pointer), pointer, keyword, message));
                }
            }
        }

        if (errors.Count == 0)
        {
            errors.Add(new SchemaError(string.Empty, string.Empty, "schema", "The output does not fit the target schema."));
        }

        return errors;
    }

    /// <summary>A JSON Pointer as a path (<c>/prices/379/price</c> is <c>prices[379].price</c>).</summary>
    public static string ToPath(string location)
    {
        var result = new StringBuilder();
        foreach (var raw in location.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                result.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else
            {
                if (result.Length > 0)
                {
                    result.Append('.');
                }

                result.Append(segment);
            }
        }

        return result.ToString();
    }
}
