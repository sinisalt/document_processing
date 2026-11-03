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
/// checked the same way), and the target-schema check in the renderer process.
/// </summary>
public sealed class OutputRenderService(DocProcDbContext db, IOutputRenderer renderer)
{
    /// <summary>The most entries of one lookup table a render receives.</summary>
    public const int MaxLookupEntries = 50_000;

    public async Task<RenderResult> RenderAsync(string engine, string? expression, JsonObject? targetSchema, JsonObject input, JsonObject? fields, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = engine == OutputEngines.Passthrough ? "$" : expression ?? string.Empty;
        var lookups = await LookupsForAsync(text, cancellationToken).ConfigureAwait(false);
        return await renderer.RenderAsync(
            new RenderRequest(text, input, today) { Lookups = lookups, Fields = fields, TargetSchema = targetSchema },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the expression calls <c>$field</c>: only then are the envelopes worth building and sending.</summary>
    public static bool UsesField(string? expression) => expression?.Contains("$field", StringComparison.Ordinal) == true;

    /// <summary>
    /// The lookup tables whose key the expression names as a string literal (<c>"erp-uom"</c> or <c>'erp-uom'</c>),
    /// as the current tenant scope sees them: its own table of a key before the platform's.
    /// </summary>
    public async Task<IReadOnlyList<LookupTableSnapshot>> LookupsForAsync(string expression, CancellationToken cancellationToken)
    {
        if (!expression.Contains("$table", StringComparison.Ordinal))
        {
            return [];
        }

        var keys = await db.LookupTables.AsNoTracking().Select(t => t.Key).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        keys.AddRange(CodeLists.Platform.Keys);
        var named = keys.Distinct(StringComparer.Ordinal).Where(k => Names(expression, k)).ToList();
        if (named.Count == 0)
        {
            return [];
        }

        var tables = await db.LookupTables.AsNoTracking().Where(t => named.Contains(t.Key))
            .Select(t => new { t.Id, t.TenantId, t.Key, t.CaseSensitive })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LookupTableSnapshot>();
        foreach (var key in named)
        {
            var table = tables.Where(t => t.Key == key).OrderBy(t => t.TenantId is null ? 1 : 0).FirstOrDefault();
            if (table is not null)
            {
                var entries = await db.LookupEntries.AsNoTracking().Where(e => e.TableId == table.Id)
                    .OrderBy(e => e.Id).Take(MaxLookupEntries)
                    .Select(e => new LookupEntrySnapshot(e.From, e.To, e.ValidFrom, e.ValidTo))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                result.Add(new LookupTableSnapshot(key, table.CaseSensitive, entries));
            }
            else if (CodeLists.Platform.TryGetValue(key, out var list))
            {
                result.Add(new LookupTableSnapshot(key, false, [.. list.Entries.Select(e => new LookupEntrySnapshot(e, list.Resolve(e)!))]));
            }
        }

        return result;
    }

    private static bool Names(string expression, string key) =>
        expression.Contains($"\"{key}\"", StringComparison.Ordinal) || expression.Contains($"'{key}'", StringComparison.Ordinal);
}
