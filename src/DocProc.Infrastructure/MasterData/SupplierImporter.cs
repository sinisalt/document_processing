using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Domain.Common;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.MasterData;

/// <summary>The conflict policies of docs/11 §14, per attribute.</summary>
public static class ConflictPolicies
{
    /// <summary>The external system's value replaces the local one (the default; docs/11 names it the default for ERP ids).</summary>
    public const string ExternalWins = "externalWins";

    /// <summary>A local value is kept; the external one only fills an empty field.</summary>
    public const string LocalWins = "localWins";

    /// <summary>Two different values are a conflict the report lists; nothing changes for that attribute.</summary>
    public const string Flag = "flag";

    public static readonly IReadOnlyList<string> All = [ExternalWins, LocalWins, Flag];
}

/// <summary>The match keys of docs/11 §14, in the default order.</summary>
public static class MatchKeys
{
    public const string ErpVendorId = "attributes.erpVendorId";
    public const string VatId = "vatId";
    public const string Iban = "iban";

    public static readonly IReadOnlyList<string> Default = [ErpVendorId, VatId, Iban];

    public static bool IsValid(string key) => key is VatId or Iban || (key.StartsWith("attributes.", StringComparison.Ordinal) && key.Length is > 11 and <= 75);
}

/// <summary>One supplier record of an import, in our shape.</summary>
public sealed record SupplierRecord(string? Name, string? VatId, string? TaxId, IReadOnlyList<string>? Ibans, string? Currency, JsonObject? Attributes);

/// <summary>One record's line of the report.</summary>
public sealed record MasterDataRow(int Row, string Action, Guid? SupplierId, string? Name, string? MatchedBy, IReadOnlyList<string> Changes, IReadOnlyList<string> Conflicts, string? Error);

/// <summary>The report of a run (<c>MasterDataRun.ReportJson</c>): the counts and one row per record (at most <see cref="MaxRowsListed"/>).</summary>
public sealed record MasterDataReport(int Created, int Updated, int Unchanged, int Conflicts, int Errors, IReadOnlyList<MasterDataRow> Rows, string? Failure = null)
{
    public const int MaxRowsListed = 1000;

    public static MasterDataReport Failed(string reason) => new(0, 0, 0, 0, 0, [], reason);

    public string ToJson() => JsonSerializer.Serialize(this, MasterDataJson.Options);
}

/// <summary>The actions of a report row.</summary>
public static class MasterDataActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Unchanged = "unchanged";
    public const string Conflict = "conflict";
    public const string Error = "error";
}

internal static class MasterDataJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// The supplier import and sync of docs/11 §14: records in our shape (<see cref="SupplierRecord"/>) are matched to the
/// tenant's suppliers by the match keys in order (an ERP vendor id in the attributes, the VAT id, an IBAN), and each
/// becomes a <c>create</c>, an <c>update</c> (the attributes that differ, under the conflict policy of each),
/// <c>unchanged</c>, a <c>conflict</c> (several suppliers match, or a <c>flag</c> attribute differs) or an <c>error</c>
/// (no name for a new supplier, a VAT id or IBAN that does not read, two records for one supplier). It is idempotent:
/// the same records a second time change nothing and report every row unchanged. A dry run computes the report only;
/// otherwise the changes are added to the context, unsaved, so they commit with the run row.
/// </summary>
public static partial class SupplierImporter
{
    public const int MaxRecords = CsvTable.MaxRows;
    public const int MaxNameLength = 200;
    public const int MaxIbans = 20;
    public const int MaxAttributes = 50;

    /// <summary>The policy of <paramref name="attribute"/> (<c>name</c>, <c>vatId</c>, <c>taxId</c>, <c>ibans</c>, <c>currency</c>, <c>attributes.&lt;key&gt;</c>): its own, else <c>attributes</c> for an attribute, else <c>*</c>, else externalWins.</summary>
    public static string PolicyOf(IReadOnlyDictionary<string, string>? policies, string attribute)
    {
        if (policies is null)
        {
            return ConflictPolicies.ExternalWins;
        }

        if (policies.TryGetValue(attribute, out var own))
        {
            return own;
        }

        if (attribute.StartsWith("attributes.", StringComparison.Ordinal) && policies.TryGetValue("attributes", out var group))
        {
            return group;
        }

        return policies.TryGetValue("*", out var all) ? all : ConflictPolicies.ExternalWins;
    }

    public static async Task<MasterDataReport> ImportAsync(
        DocProcDbContext db,
        Guid tenantId,
        IReadOnlyList<SupplierRecord> records,
        IReadOnlyList<string>? matchKeys,
        IReadOnlyDictionary<string, string>? policies,
        bool dryRun,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count > MaxRecords)
        {
            return MasterDataReport.Failed($"An import holds at most {MaxRecords} records; this one has {records.Count}.");
        }

        var keys = matchKeys is { Count: > 0 } given ? given : MatchKeys.Default;
        var suppliers = await db.Suppliers.Where(s => s.TenantId == tenantId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var attributesOf = suppliers.ToDictionary(s => s.Id, s => Parse(s.AttributesJson));
        var claimed = new Dictionary<Guid, int>();
        var newNames = new HashSet<string>(suppliers.Select(s => s.NormalizedName), StringComparer.Ordinal);
        var rows = new List<MasterDataRow>();
        int created = 0, updated = 0, unchanged = 0, conflicts = 0, errors = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var row = i + 1;
            var record = Clean(records[i], out var problem);
            if (problem is not null)
            {
                errors++;
                Add(rows, new MasterDataRow(row, MasterDataActions.Error, null, records[i].Name, null, [], [], problem));
                continue;
            }

            // Match: the first key that finds anyone decides; more than one supplier for it is a conflict.
            List<Supplier> matches = [];
            string? matchedBy = null;
            foreach (var key in keys)
            {
                matches = key switch
                {
                    MatchKeys.VatId when record.VatId is { } vat => [.. suppliers.Where(s => s.VatId == vat)],
                    MatchKeys.Iban when record.Ibans is { Count: > 0 } ibans => [.. suppliers.Where(s => s.Ibans.Any(ibans.Contains))],
                    _ when key.StartsWith("attributes.", StringComparison.Ordinal) && Text(record.Attributes?[key["attributes.".Length..]]) is { } wanted
                        => [.. suppliers.Where(s => Text(attributesOf[s.Id]?[key["attributes.".Length..]]) == wanted)],
                    _ => [],
                };
                if (matches.Count > 0)
                {
                    matchedBy = key;
                    break;
                }
            }

            if (matches.Count > 1)
            {
                conflicts++;
                Add(rows, new MasterDataRow(row, MasterDataActions.Conflict, null, record.Name, matchedBy, [], [$"{matchedBy} matches {matches.Count} suppliers: {string.Join(", ", matches.Take(3).Select(m => m.Name))}"], null));
                continue;
            }

            if (matches.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(record.Name))
                {
                    errors++;
                    Add(rows, new MasterDataRow(row, MasterDataActions.Error, null, null, null, [], [], "A new supplier needs a name."));
                    continue;
                }

                var normalized = Identifiers.NormalizeName(record.Name);
                if (normalized.Length == 0 || !newNames.Add(normalized))
                {
                    conflicts++;
                    Add(rows, new MasterDataRow(row, MasterDataActions.Conflict, null, record.Name, "name", [], [$"A supplier whose name folds to '{normalized}' exists already (no match key found it); add its {keys[0]} to the record or to the supplier."], null));
                    continue;
                }

                var supplier = new Supplier
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Name = record.Name.Trim(),
                    NormalizedName = normalized,
                    VatId = record.VatId,
                    TaxId = record.TaxId,
                    Ibans = [.. record.Ibans ?? []],
                    DefaultCurrency = record.Currency,
                    AttributesJson = record.Attributes is { Count: > 0 } a ? a.ToJsonString() : null,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                if (!dryRun)
                {
                    db.Suppliers.Add(supplier);
                }

                suppliers.Add(supplier);
                attributesOf[supplier.Id] = record.Attributes;
                claimed[supplier.Id] = row;
                created++;
                Add(rows, new MasterDataRow(row, MasterDataActions.Create, dryRun ? null : supplier.Id, supplier.Name, null, [], [], null));
                continue;
            }

            var target = matches[0];
            if (claimed.TryGetValue(target.Id, out var earlierRow))
            {
                errors++;
                Add(rows, new MasterDataRow(row, MasterDataActions.Error, target.Id, record.Name, matchedBy, [], [], $"Row {earlierRow} already wrote this supplier."));
                continue;
            }

            claimed[target.Id] = row;
            var changes = new List<string>();
            var flagged = new List<string>();
            var localAttributes = attributesOf[target.Id] ?? new JsonObject();
            var nextAttributes = (JsonObject)localAttributes.DeepClone();
            string? nextName = Decide("name", target.Name, record.Name, policies, changes, flagged);
            string? nextVat = Decide("vatId", target.VatId, record.VatId, policies, changes, flagged);
            string? nextTax = Decide("taxId", target.TaxId, record.TaxId, policies, changes, flagged);
            string? nextCurrency = Decide("currency", target.DefaultCurrency, record.Currency, policies, changes, flagged);
            var nextIbans = target.Ibans;
            if (record.Ibans is { Count: > 0 } incoming && !incoming.Order(StringComparer.Ordinal).SequenceEqual(target.Ibans.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                var policy = PolicyOf(policies, "ibans");
                if (policy == ConflictPolicies.ExternalWins || target.Ibans.Count == 0)
                {
                    nextIbans = [.. incoming];
                    changes.Add("ibans");
                }
                else if (policy == ConflictPolicies.Flag)
                {
                    flagged.Add($"ibans: local {string.Join(" ", target.Ibans)}, external {string.Join(" ", incoming)}");
                }
            }

            foreach (var (key, value) in record.Attributes ?? [])
            {
                var local = Text(localAttributes[key]) ?? localAttributes[key]?.ToJsonString();
                var external = Text(value) ?? value?.ToJsonString();
                if (external is null || local == external)
                {
                    continue;
                }

                var policy = PolicyOf(policies, $"attributes.{key}");
                if (local is null || policy == ConflictPolicies.ExternalWins)
                {
                    nextAttributes[key] = value?.DeepClone();
                    changes.Add($"attributes.{key}");
                }
                else if (policy == ConflictPolicies.Flag)
                {
                    flagged.Add($"attributes.{key}: local {local}, external {external}");
                }
            }

            if (nextAttributes.Count > MaxAttributes)
            {
                errors++;
                Add(rows, new MasterDataRow(row, MasterDataActions.Error, target.Id, target.Name, matchedBy, [], [], $"The supplier would hold more than {MaxAttributes} attributes."));
                continue;
            }

            var renamed = nextName is not null && nextName != target.Name ? Identifiers.NormalizeName(nextName) : null;
            if (renamed is not null && renamed != target.NormalizedName && suppliers.Any(s => s.Id != target.Id && s.NormalizedName == renamed))
            {
                flagged.Add($"name: '{nextName}' folds to the name of another supplier; kept '{target.Name}'.");
                changes.Remove("name");
                nextName = target.Name;
                renamed = null;
            }

            if (changes.Count > 0 && !dryRun)
            {
                target.Name = nextName ?? target.Name;
                if (renamed is not null)
                {
                    target.NormalizedName = renamed;
                }

                target.VatId = nextVat;
                target.TaxId = nextTax;
                target.DefaultCurrency = nextCurrency;
                target.Ibans = [.. nextIbans];
                target.AttributesJson = nextAttributes.Count == 0 ? null : nextAttributes.ToJsonString();
                target.UpdatedAt = now;
            }

            if (flagged.Count > 0)
            {
                conflicts++;
            }

            if (changes.Count > 0)
            {
                updated++;
            }
            else if (flagged.Count == 0)
            {
                unchanged++;
            }

            var action = changes.Count > 0 ? MasterDataActions.Update : flagged.Count > 0 ? MasterDataActions.Conflict : MasterDataActions.Unchanged;
            Add(rows, new MasterDataRow(row, action, target.Id, target.Name, matchedBy, changes, flagged, null));
        }

        return new MasterDataReport(created, updated, unchanged, conflicts, errors, rows);
    }

    /// <summary>One plain attribute under its policy: the value to keep, recording a change or a conflict.</summary>
    private static string? Decide(string attribute, string? local, string? external, IReadOnlyDictionary<string, string>? policies, List<string> changes, List<string> flagged)
    {
        if (external is null || string.Equals(local, external, StringComparison.Ordinal))
        {
            return local;
        }

        if (string.IsNullOrEmpty(local))
        {
            changes.Add(attribute);
            return external;
        }

        switch (PolicyOf(policies, attribute))
        {
            case ConflictPolicies.LocalWins:
                return local;
            case ConflictPolicies.Flag:
                flagged.Add($"{attribute}: local '{local}', external '{external}'");
                return local;
            default:
                changes.Add(attribute);
                return external;
        }
    }

    private static void Add(List<MasterDataRow> rows, MasterDataRow row)
    {
        if (rows.Count < MasterDataReport.MaxRowsListed)
        {
            rows.Add(row);
        }
    }

    /// <summary>A record trimmed and normalised as suppliers are stored; the problem when a value does not read.</summary>
    public static SupplierRecord Clean(SupplierRecord record, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(record);
        problem = null;
        var name = string.IsNullOrWhiteSpace(record.Name) ? null : record.Name.Trim();
        if (name is { Length: > MaxNameLength })
        {
            problem = $"name is longer than {MaxNameLength} characters.";
        }

        var vat = Identifiers.NormalizeCompact(record.VatId);
        if (vat is { Length: > 32 })
        {
            problem ??= "vatId is longer than 32 characters.";
        }

        var tax = string.IsNullOrWhiteSpace(record.TaxId) ? null : record.TaxId.Trim();
        if (tax is { Length: > 32 })
        {
            problem ??= "taxId is longer than 32 characters.";
        }

        var ibans = (record.Ibans ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => Identifiers.NormalizeCompact(i)!).Distinct(StringComparer.Ordinal).ToList();
        if (ibans.Count > MaxIbans)
        {
            problem ??= $"at most {MaxIbans} IBANs.";
        }
        else if (ibans.FirstOrDefault(i => !IbanPattern().IsMatch(i)) is { } bad)
        {
            problem ??= $"'{bad}' is not an IBAN.";
        }

        var currency = string.IsNullOrWhiteSpace(record.Currency) ? null : record.Currency.Trim().ToUpperInvariant();
        if (currency is not null && !CurrencyPattern().IsMatch(currency))
        {
            problem ??= $"'{currency}' is not a three-letter currency code.";
        }

        JsonObject? attributes = null;
        if (record.Attributes is { Count: > 0 } given)
        {
            attributes = new JsonObject();
            foreach (var (key, value) in given)
            {
                if (key.Length is 0 or > 64 || value is JsonObject or JsonArray)
                {
                    problem ??= $"attribute '{key}' must be a key of at most 64 characters with a plain value.";
                    continue;
                }

                if (value is not null && !(value is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)))
                {
                    attributes[key] = value.DeepClone();
                }
            }

            if (attributes.Count > MaxAttributes)
            {
                problem ??= $"at most {MaxAttributes} attributes.";
            }
        }

        return new SupplierRecord(name, vat, tax, ibans.Count == 0 ? null : ibans, currency, attributes is { Count: > 0 } ? attributes : null);
    }

    /// <summary>
    /// Records from a CSV table and a column map (<c>{name: "Name", vatId: "USt-IdNr", taxId, ibans, currency,
    /// "attributes.erpVendorId": "Kreditor"}</c>, header names compared without case): several IBANs in one cell are
    /// separated by <c>;</c>, <c>,</c> or white space.
    /// </summary>
    public static IReadOnlyList<SupplierRecord> FromCsv(IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyDictionary<string, string> columns, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        problem = null;
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (target, column) in columns)
        {
            var at = header.ToList().FindIndex(h => string.Equals(h, column.Trim(), StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                problem = $"The file has no column '{column}' (for {target}); it has {string.Join(", ", header.Take(20))}.";
                return [];
            }

            index[target] = at;
        }

        string? Cell(IReadOnlyList<string> row, string target) =>
            index.TryGetValue(target, out var at) && at < row.Count && !string.IsNullOrWhiteSpace(row[at]) ? row[at].Trim() : null;

        var result = new List<SupplierRecord>(rows.Count);
        foreach (var row in rows)
        {
            JsonObject? attributes = null;
            foreach (var target in index.Keys.Where(k => k.StartsWith("attributes.", StringComparison.Ordinal)))
            {
                if (Cell(row, target) is { } value)
                {
                    attributes ??= [];
                    attributes[target["attributes.".Length..]] = value;
                }
            }

            var ibans = Cell(row, "ibans")?.Split([';', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            result.Add(new SupplierRecord(Cell(row, "name"), Cell(row, "vatId"), Cell(row, "taxId"), ibans, Cell(row, "currency"), attributes));
        }

        return result;
    }

    /// <summary>Records from JSON (an array of <c>{name, vatId, taxId, ibans | iban, currency, attributes}</c>), the problem when it is not that.</summary>
    public static IReadOnlyList<SupplierRecord> FromJson(JsonNode? node, out string? problem)
    {
        problem = null;
        if (node is JsonObject wrapper && wrapper["records"] is JsonArray inner)
        {
            node = inner;
        }

        if (node is not JsonArray array)
        {
            problem = "The records are not a JSON array of supplier records.";
            return [];
        }

        if (array.Count > MaxRecords)
        {
            problem = $"An import holds at most {MaxRecords} records; this one has {array.Count}.";
            return [];
        }

        var result = new List<SupplierRecord>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                result.Add(new SupplierRecord(null, null, null, null, null, null));
                continue;
            }

            var ibans = obj["ibans"] is JsonArray list
                ? list.Select(Text).OfType<string>().ToList()
                : Text(obj["iban"]) is { } one ? [one] : null;
            result.Add(new SupplierRecord(Text(obj["name"]), Text(obj["vatId"]), Text(obj["taxId"]), ibans, Text(obj["currency"]), obj["attributes"] as JsonObject));
        }

        return result;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        _ => null,
    };

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex IbanPattern();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();

    internal static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
