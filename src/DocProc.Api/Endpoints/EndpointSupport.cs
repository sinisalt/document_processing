using System.Text;
using System.Text.RegularExpressions;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Endpoints;

/// <summary>One page of a cursor-paginated list (docs/05 "cursor pagination"); <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Helpers shared by the tenancy endpoints: the tenant guard, opaque cursors and the hand-rolled validators.</summary>
internal static partial class EndpointSupport
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();

    /// <summary>
    /// Tenant-scoped resources need a tenant: a platform user who sent no X-Tenant-Id is refused with 400, the same
    /// way <see cref="ApiKeyEndpoints"/> does, so no endpoint ever guesses a tenant.
    /// </summary>
    public static ProblemHttpResult? RequireTenant(ITenantContext tenant, string what) =>
        tenant.TenantId is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Tenant required", detail: $"{what} belong to a tenant. Platform users must send X-Tenant-Id.")
            : null;

    /// <summary>The current tenant row through the request-scoped (filtered) context, or null when the resolved id does not exist.</summary>
    public static Task<Tenant?> CurrentTenantAsync(DocProcDbContext db, CancellationToken ct) =>
        db.Tenants.SingleOrDefaultAsync(ct);

    public static ProblemHttpResult UnknownTenant() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown tenant", detail: "The resolved tenant does not exist.");

    /// <summary>
    /// The development SQLite provider (docs/09) stores DateTimeOffset as text and refuses it in ORDER BY and
    /// comparisons; endpoints that sort or filter by a timestamp do so in SQL on SQL Server and in memory on SQLite.
    /// </summary>
    public static bool UsesSqlite(DocProcDbContext db) => db.Database.IsSqlite();

    public static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    public static int ClampLimit(int? limit) => limit is null or <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    /// <summary>Opaque cursor: the sort key(s) of the last item of the previous page, joined by a newline and base64url-encoded.</summary>
    public static string EncodeCursor(params string[] parts) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(string.Join('\n', parts)));

    /// <summary>Null for no cursor, the parts for a well-formed one, an empty array for one this API never issued.</summary>
    public static string[]? DecodeCursor(string? cursor, int parts)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor)).Split('\n');
            return decoded.Length == parts ? decoded : [];
        }
        catch (FormatException)
        {
            return [];
        }
    }

    public static ValidationProblem InvalidCursor() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["cursor"] = ["The cursor is not one this endpoint issued."] });

    /// <summary>A LIKE pattern that matches the term literally anywhere; use with escape character <c>\</c>.</summary>
    public static string LikeContains(string term) =>
        "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";

    public static bool IsSlug(string? value) => value is not null && value.Length <= 64 && SlugPattern().IsMatch(value);

    public static bool IsCurrency(string? value) => value is not null && CurrencyPattern().IsMatch(value);

    public static bool IsCountry(string? value) => value is not null && CountryPattern().IsMatch(value);

    public static bool IsTimeZone(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(value, out _);

    public static bool IsAbsoluteHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public static bool TryParseEnum<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    /// <summary>For values <see cref="TryParseEnum{TEnum}"/> already accepted; throws on anything else so a missed validation cannot store a default.</summary>
    public static TEnum ParseEnum<TEnum>(string? value)
        where TEnum : struct, Enum =>
        TryParseEnum<TEnum>(value, out var parsed) ? parsed : throw new ArgumentException($"'{value}' is not a {typeof(TEnum).Name}.", nameof(value));

    public static string Names<TEnum>()
        where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetNames<TEnum>());

    /// <summary>Trimmed, de-duplicated, empty entries dropped.</summary>
    public static List<string> CleanList(IEnumerable<string>? values) =>
        values is null ? [] : values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.Ordinal).ToList();

    public static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Collects field errors in the shape <c>TypedResults.ValidationProblem</c> expects.</summary>
    public sealed class Errors
    {
        private readonly Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);

        public int Count => _errors.Count;

        public Errors Add(string field, string message)
        {
            _errors[field] = [message];
            return this;
        }

        public Errors Require(string field, string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
            {
                Add(field, $"{field} is required and at most {maxLength} characters.");
            }

            return this;
        }

        public Errors Optional(string field, string? value, int maxLength)
        {
            if (value is not null && value.Trim().Length > maxLength)
            {
                Add(field, $"{field} is at most {maxLength} characters.");
            }

            return this;
        }

        public ValidationProblem? ToProblem() => _errors.Count == 0 ? null : TypedResults.ValidationProblem(_errors);
    }
}
