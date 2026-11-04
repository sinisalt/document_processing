using System.Globalization;
using System.Text.Json.Nodes;
using DocProc.Application.Outputs;
using DocProc.Domain.Validation;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Outputs;

/// <summary>
/// One render of a mapping version over a values view, as preview and delivery both do it (docs/11 §13.1 "Preview is
/// always rendered by the server, so preview and delivery cannot drift"): the lookup tables the expression names
/// (the tenant's, then the platform's rows, then the platform code lists in code, <c>uom</c>), the engine
/// (<c>jsonata</c>, or <c>passthrough</c>, the view itself, rendered as <c>$</c> so its size and target schema are
/// checked the same way), and the target-schema check in the renderer process. The lookup data one render receives is
/// bounded in total (<see cref="RenderLimits.MaxLookupEntriesPerRender"/> entries,
/// <see cref="RenderLimits.MaxLookupCharactersPerRender"/> characters): a render naming more fails for good with a
/// <c>function</c> error, as it would every time.
/// </summary>
public sealed class OutputRenderService(DocProcDbContext db, IOutputRenderer renderer)
{
    /// <summary>The most entries of one lookup table a render receives.</summary>
    public const int MaxLookupEntries = 50_000;

    /// <summary>The most lookup entries one render receives over all its tables (<see cref="RenderLimits.MaxLookupEntriesPerRender"/>; smaller in tests).</summary>
    internal int EntryBudget { get; init; } = RenderLimits.MaxLookupEntriesPerRender;

    /// <summary>The most lookup characters one render receives over all its tables (<see cref="RenderLimits.MaxLookupCharactersPerRender"/>; smaller in tests).</summary>
    internal long CharacterBudget { get; init; } = RenderLimits.MaxLookupCharactersPerRender;

    public async Task<RenderResult> RenderAsync(string engine, string? expression, JsonObject? targetSchema, JsonObject input, JsonObject? fields, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = engine == OutputEngines.Passthrough ? "$" : expression ?? string.Empty;
        var lookups = await LookupsForAsync(text, cancellationToken).ConfigureAwait(false);
        if (lookups.Error is { } tooMuch)
        {
            return RenderResult.Failed(tooMuch, TimeSpan.Zero, tables: [.. lookups.Tables.Select(t => t.Key)]);
        }

        return await renderer.RenderAsync(
            new RenderRequest(text, input, today) { Lookups = lookups.Tables, Fields = fields, TargetSchema = targetSchema },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The lookup tables a render receives, or the error when together they pass the per-render bound.</summary>
    public sealed record LookupLoad(IReadOnlyList<LookupTableSnapshot> Tables, RenderError? Error);

    /// <summary>Whether the expression calls <c>$field</c>: only then are the envelopes worth building and sending.</summary>
    public static bool UsesField(string? expression) => expression?.Contains("$field", StringComparison.Ordinal) == true;

    /// <summary>
    /// The lookup tables whose key the expression names as a string literal (<c>"erp-uom"</c> or <c>'erp-uom'</c>),
    /// as the current tenant scope sees them: its own table of a key before the platform's. Loading stops at the first
    /// table that takes the total past the per-render bound, which is then the error.
    /// </summary>
    public async Task<LookupLoad> LookupsForAsync(string expression, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!expression.Contains("$table", StringComparison.Ordinal))
        {
            return new LookupLoad([], null);
        }

        var keys = await db.LookupTables.AsNoTracking().Select(t => t.Key).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        keys.AddRange(CodeLists.Platform.Keys);
        var named = keys.Distinct(StringComparer.Ordinal).Where(k => Names(expression, k)).ToList();
        if (named.Count == 0)
        {
            return new LookupLoad([], null);
        }

        var tables = await db.LookupTables.AsNoTracking().Where(t => named.Contains(t.Key))
            .Select(t => new { t.Id, t.TenantId, t.Key, t.CaseSensitive })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LookupTableSnapshot>();
        var entryCount = 0;
        long characters = 0;
        foreach (var key in named)
        {
            var table = tables.Where(t => t.Key == key).OrderBy(t => t.TenantId is null ? 1 : 0).FirstOrDefault();
            LookupTableSnapshot snapshot;
            if (table is not null)
            {
                // One more than the entries still allowed, so passing the bound is seen without loading past it.
                var allowed = Math.Min(MaxLookupEntries, EntryBudget - entryCount + 1);
                var entries = await db.LookupEntries.AsNoTracking().Where(e => e.TableId == table.Id)
                    .OrderBy(e => e.Id).Take(allowed)
                    .Select(e => new LookupEntrySnapshot(e.From, e.To, e.ValidFrom, e.ValidTo))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                snapshot = new LookupTableSnapshot(key, table.CaseSensitive, entries);
            }
            else if (CodeLists.Platform.TryGetValue(key, out var list))
            {
                snapshot = new LookupTableSnapshot(key, false, [.. list.Entries.Select(e => new LookupEntrySnapshot(e, list.Resolve(e)!))]);
            }
            else
            {
                continue;
            }

            result.Add(snapshot);
            entryCount += snapshot.Entries.Count;
            characters += snapshot.Entries.Sum(e => (long)e.From.Length + e.To.Length);
            if (entryCount > EntryBudget || characters > CharacterBudget)
            {
                return new LookupLoad(result, new RenderError(
                    RenderErrorKinds.Function,
                    "LOOKUPS",
                    RenderLimits.Shorten($"$table: the lookup tables the expression names ({string.Join(", ", named)}) hold more than {EntryBudget.ToString(CultureInfo.InvariantCulture)} entries or {CharacterBudget.ToString(CultureInfo.InvariantCulture)} characters together, more than one render receives.")));
            }
        }

        return new LookupLoad(result, null);
    }

    private static bool Names(string expression, string key) =>
        expression.Contains($"\"{key}\"", StringComparison.Ordinal) || expression.Contains($"'{key}'", StringComparison.Ordinal);
}
