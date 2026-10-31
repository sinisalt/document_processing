using System.Globalization;
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

    /// <summary>
    /// Accepts an enum member by name, case-insensitively, and nothing else: <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>
    /// would also accept the numeric value ("0" for the first member), which a client never means.
    /// </summary>
    public static bool TryParseEnum<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        if (value is not null)
        {
            var trimmed = value.Trim();
            foreach (var name in Enum.GetNames<TEnum>())
            {
                if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    parsed = Enum.Parse<TEnum>(name);
                    return true;
                }
            }
        }

        parsed = default;
        return false;
    }

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

    /// <summary>
    /// Collects field errors in the shape <c>TypedResults.ValidationProblem</c> expects. An error added with a code
    /// (<see cref="ValidationCodes"/>) also goes out in the problem's <c>errorCodes</c> extension,
    /// <c>{ field: { code, params } }</c> with invariant string parameters, so the apps can say it in the UI language;
    /// <c>errors</c> keeps the English text for every other client (docs/05 "Errors").
    /// </summary>
    public sealed class Errors
    {
        private readonly Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, object>> _codes = new(StringComparer.Ordinal);

        public int Count => _errors.Count;

        public Errors Add(string field, string message, string? code = null, IReadOnlyDictionary<string, string>? parameters = null)
        {
            _errors[field] = [message];
            if (code is null)
            {
                _codes.Remove(field);
            }
            else
            {
                _codes[field] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["code"] = code,
                    ["params"] = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                };
            }

            return this;
        }

        public Errors Require(string field, string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
            {
                Add(field, $"{field} is required and at most {maxLength} characters.", ValidationCodes.RequiredMaxLength, MaxLengthParams(maxLength));
            }

            return this;
        }

        public Errors Optional(string field, string? value, int maxLength)
        {
            if (value is not null && value.Trim().Length > maxLength)
            {
                Add(field, $"{field} is at most {maxLength} characters.", ValidationCodes.MaxLength, MaxLengthParams(maxLength));
            }

            return this;
        }

        public ValidationProblem? ToProblem() =>
            _errors.Count == 0
                ? null
                : TypedResults.ValidationProblem(_errors, extensions: _codes.Count == 0 ? null : new Dictionary<string, object?>(StringComparer.Ordinal) { ["errorCodes"] = _codes });

        private static Dictionary<string, string> MaxLengthParams(int maxLength) =>
            new(StringComparer.Ordinal) { ["maxLength"] = maxLength.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>
    /// The codes of <c>errorCodes</c> (docs/05 "Errors"). Stable strings, like flag codes: the apps translate them
    /// (<c>common:apiErrors.&lt;code&gt;</c>) and fall back to the English message for one they do not know.
    /// </summary>
    public static class ValidationCodes
    {
        /// <summary>Blank, or longer than <c>maxLength</c> characters.</summary>
        public const string RequiredMaxLength = "requiredMaxLength";

        /// <summary>Longer than <c>maxLength</c> characters.</summary>
        public const string MaxLength = "maxLength";

        /// <summary>Given, but blank (a rename to whitespace).</summary>
        public const string NotBlank = "notBlank";

        /// <summary>Not a three-letter upper-case ISO 4217 code.</summary>
        public const string CurrencyCode = "currencyCode";

        /// <summary>Not a two-letter upper-case ISO 3166-1 code.</summary>
        public const string CountryCode = "countryCode";

        /// <summary>Not an IANA time zone id.</summary>
        public const string TimeZone = "timeZone";

        /// <summary>Not a slug (lowercase letters, digits and single dashes, at most <c>maxLength</c>).</summary>
        public const string Slug = "slug";

        /// <summary>Outside <c>min</c>..<c>max</c>.</summary>
        public const string Range = "range";

        /// <summary>Zero or negative.</summary>
        public const string Positive = "positive";

        /// <summary>Not an absolute http(s) URL of at most <c>maxLength</c> characters.</summary>
        public const string HttpUrl = "httpUrl";

        /// <summary>A code's parameters, numbers as invariant strings.</summary>
        public static IReadOnlyDictionary<string, string> Params(params (string Key, IFormattable Value)[] pairs) =>
            pairs.ToDictionary(p => p.Key, p => p.Value.ToString(null, CultureInfo.InvariantCulture), StringComparer.Ordinal);
    }
}
