using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Application.Outputs;
using DocProc.Definitions.Pipelines;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Outputs.Lookups;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Layouts;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Stages;

/// <summary>What the <c>http-lookup</c> steps of one Extract run did: their flags, <c>appliedConfig.lookups</c> and the timeline summary.</summary>
internal sealed record LookupRun(IReadOnlyList<DocumentFlag> Flags, JsonObject? Applied, object? Summary)
{
    public static LookupRun None { get; } = new([], null, null);
}

/// <summary>
/// Runs the enabled <c>http-lookup</c> steps of the document's pipeline (docs/11 §10.2) on the tree Extract built, in
/// their order, each over the values view of the tree as the steps before it left it, and writes their answers into it
/// as <c>Inferred</c> (<see cref="SourceMethod.Lookup"/>, <c>mode: fillMissing</c> by default, never over a value a
/// reviewer corrected). A failure is a <c>LOOKUP_FAILED</c> flag (a warning, blocking with <c>onFailure: block</c>,
/// none with <c>ignore</c>), never a failed stage.
/// <para>
/// Correction (P5): the built-in extraction steps (<c>xml</c>, <c>map</c>, <c>prefill</c>, <c>llm</c>) run as one
/// chain; an <c>http-lookup</c> placed anywhere in Extract runs after that chain, in its order among the steps added to
/// the phase, so it reads what the extraction found and fills what is still missing.
/// </para>
/// </summary>
internal sealed class ExtractLookups(IServiceProvider services)
{
    /// <summary>The confidence of a value a lookup wrote: a tenant system said so, so it is not low confidence.</summary>
    public const double Confidence = 0.95;

    public async Task<LookupRun> RunAsync(Document document, EffectiveType? type, ResolvedPipeline pipeline, JsonObject tree, bool typedRecord, string tenantCurrency, CancellationToken cancellationToken)
    {
        var steps = pipeline.Phase(PipelinePhases.Extract).Where(s => s.Enabled && s.Kind == StepKindCatalogue.HttpLookup).ToList();
        if (steps.Count == 0 || type?.Compiled is not { } compiled)
        {
            return LookupRun.None;
        }

        var lookups = services.GetService<HttpLookupService>();
        var views = services.GetService<IDocumentDataViewBuilder>();
        var flags = new List<DocumentFlag>();
        var applied = new JsonObject();
        var summary = new List<object>();
        foreach (var step in steps)
        {
            LookupOutcome outcome;
            if (lookups is null || views is null)
            {
                outcome = LookupOutcome.Failed("unavailable", "This host has no outputs (destinations) to call.");
            }
            else
            {
                var view = await views.BuildAsync(new DataViewRequest(document.Id) { CanonicalJson = tree.ToJsonString(Domain.Documents.CanonicalJson.Options), TypeVersionId = document.DocumentTypeVersionId }, cancellationToken).ConfigureAwait(false);
                outcome = view is null
                    ? LookupOutcome.Failed("unavailable", "The document's values could not be read.")
                    : await lookups.RunAsync(new LookupCall(document.TenantId, document.Id, document.Version, document.Type.Value, step, view.View, view.Today), cancellationToken).ConfigureAwait(false);
            }

            if (!outcome.Succeeded)
            {
                Fail(step, outcome.Reason ?? "failed", outcome.Message ?? "The lookup failed.", outcome.HttpStatus);
                continue;
            }

            var backup = tree.DeepClone().AsObject();
            var written = new List<string>();
            var ignored = new List<string>();
            var currency = DefinitionTree.ValueOf(tree["currency"]) is JsonValue code && code.TryGetValue<string>(out var c) ? c : tenantCurrency;
            var overrideExisting = step.Text("mode") == "override";
            foreach (var (path, value) in outcome.Assignments)
            {
                if (value is null)
                {
                    continue;
                }

                if (LayoutTreeWriter.Assign(tree, compiled.Descriptors, path, value, new FieldSource(SourceMethod.Lookup), Confidence, overrideExisting, currency, typedRecord))
                {
                    written.Add(path);
                }
                else
                {
                    ignored.Add(path);
                }
            }

            if (typedRecord && written.Count > 0 && !ReadsAsInvoice(tree))
            {
                // A value the coded invoice record cannot hold: the step's writes are taken back.
                Restore(tree, backup);
                Fail(step, "shape", $"The values the lookup gave ({string.Join(", ", written)}) do not fit the invoice's fields.", outcome.HttpStatus);
                continue;
            }

            applied[step.Id] = new JsonObject
            {
                ["status"] = "ok",
                ["cached"] = outcome.Cached,
                ["httpStatus"] = outcome.HttpStatus,
                ["written"] = new JsonArray([.. written.Select(p => (JsonNode)p)]),
                ["ignored"] = new JsonArray([.. ignored.Select(p => (JsonNode)p)]),
            };
            summary.Add(new { step = step.Id, status = "ok", outcome.Cached, written });
        }

        return new LookupRun(flags, applied, summary);

        void Fail(ResolvedStep step, string reason, string message, int? status)
        {
            applied[step.Id] = new JsonObject { ["status"] = "failed", ["reason"] = reason, ["httpStatus"] = status };
            summary.Add(new { step = step.Id, status = "failed", reason });
            var onFailure = step.Text("onFailure") ?? "warn";
            if (onFailure == "ignore")
            {
                return;
            }

            var flag = FlagCodes.Create(
                FlagCodes.LookupFailed,
                $"The lookup {step.Id} failed: {message}",
                parameters: FlagParams.Of(("stepId", step.Id), (FlagParams.Reason, reason), ("httpStatus", status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)));
            flags.Add(onFailure == "block" ? flag with { Severity = FlagSeverity.Blocking } : flag);
        }
    }

    private static bool ReadsAsInvoice(JsonObject tree)
    {
        try
        {
            _ = new DocProc.Application.Abstractions.ExtractedDocument(tree.DeepClone().AsObject()).ToInvoice();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Restore(JsonObject tree, JsonObject backup)
    {
        tree.Clear();
        foreach (var (key, value) in backup)
        {
            tree[key] = value?.DeepClone();
        }
    }
}
