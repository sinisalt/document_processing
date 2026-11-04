using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocProc.Application.Suppliers;

/// <summary>Whether a supplier's single allowed type is set without classification (docs/11 §6.4 <c>fixDocumentType</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FixDocumentType>))]
public enum FixDocumentType
{
    /// <summary>Only when the supplier was found from the source (a source rule, the channel default or a hint).</summary>
    [JsonStringEnumMemberName("whenSourceMatched")]
    WhenSourceMatched,

    /// <summary>Whenever the supplier is known at ingest.</summary>
    [JsonStringEnumMemberName("always")]
    Always,

    /// <summary>Never; the classifier decides.</summary>
    [JsonStringEnumMemberName("never")]
    Never,
}

/// <summary>How a supplier's documents are reviewed (docs/11 §6.4 <c>review.mode</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SupplierReviewMode>))]
public enum SupplierReviewMode
{
    /// <summary>The tenant's and the type's review gate decides.</summary>
    [JsonStringEnumMemberName("inherit")]
    Inherit,

    /// <summary>Every document of the supplier goes to review.</summary>
    [JsonStringEnumMemberName("always")]
    Always,
}

/// <summary>A tri-state switch that can defer to the tenant default (<c>layouts.required</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InheritableSwitch>))]
public enum InheritableSwitch
{
    [JsonStringEnumMemberName("inherit")]
    Inherit,

    [JsonStringEnumMemberName("true")]
    On,

    [JsonStringEnumMemberName("false")]
    Off,
}

/// <summary>The supplier's locale for parsing printed values (stored; the layouts' parse transforms read it, P4 part B).</summary>
public sealed record SupplierLocale(string? Culture = null, string? DateOrder = null);

/// <summary><c>review</c>: the mode and the minimum field confidence for auto-approval.</summary>
public sealed record SupplierReviewSettings(SupplierReviewMode? Mode = null, double? AutoApproveMinConfidence = null);

/// <summary><c>defaults</c>: supplier-wide values, with or without a layout.</summary>
public sealed record SupplierDefaults(string? Currency = null);

/// <summary><c>layouts</c>: whether the supplier is layout-managed before its first layout exists.</summary>
public sealed record SupplierLayoutSettings(InheritableSwitch? Required = null);

/// <summary>
/// The settings of a supplier or a supplier group (docs/11 §6.4 <c>SettingsJson</c>), every member optional: an absent
/// member is inherited (the supplier's from its group, the group's from the defaults). Stored as camelCase JSON.
/// <list type="bullet">
/// <item><see cref="AllowedTypeKeys"/> with <see cref="FixDocumentType"/>: a single allowed type is pinned at ingest
/// (<c>TypeSource=Supplier</c>) under <c>always</c>, or under <c>whenSourceMatched</c> (the default) when the source
/// named the supplier.</item>
/// <item><see cref="Review"/>: <c>mode: always</c> sends every document to review; <c>autoApproveMinConfidence</c>
/// raises the tenant's threshold for the supplier's documents.</item>
/// <item><see cref="Defaults"/>: the currency assumed when none is printed (stored; applied by the <c>defaults</c> step).</item>
/// <item><see cref="Layouts"/>: <c>required</c> (<c>true</c>, <c>false</c>, <c>inherit</c>) resolves against
/// <c>TenantSettings.LayoutsRequiredByDefault</c>.</item>
/// <item><see cref="RequireSenderAuth"/>: the default of <c>requireSenderAuth</c> for the supplier's new sender rules.</item>
/// </list>
/// </summary>
public sealed record SupplierSettings
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static SupplierSettings Empty { get; } = new();

    public string? DefaultDocumentTypeKey { get; init; }

    public IReadOnlyList<string>? AllowedTypeKeys { get; init; }

    public FixDocumentType? FixDocumentType { get; init; }

    public SupplierLocale? Locale { get; init; }

    public SupplierReviewSettings? Review { get; init; }

    public SupplierDefaults? Defaults { get; init; }

    public SupplierLayoutSettings? Layouts { get; init; }

    public bool? RequireSenderAuth { get; init; }

    /// <summary>The stored settings, or <see cref="Empty"/> for none or for a value that does not read (written outside the API).</summary>
    public static SupplierSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<SupplierSettings>(json, JsonOptions) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>The settings as stored, or null when nothing is set.</summary>
    public string? ToJson()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return json == "{}" ? null : json;
    }

    /// <summary>These settings over <paramref name="inherited"/> (a group's): every member this one leaves out comes from there.</summary>
    public SupplierSettings Over(SupplierSettings inherited)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        return new SupplierSettings
        {
            DefaultDocumentTypeKey = DefaultDocumentTypeKey ?? inherited.DefaultDocumentTypeKey,
            AllowedTypeKeys = AllowedTypeKeys ?? inherited.AllowedTypeKeys,
            FixDocumentType = FixDocumentType ?? inherited.FixDocumentType,
            Locale = Locale is null && inherited.Locale is null ? null : new SupplierLocale(Locale?.Culture ?? inherited.Locale?.Culture, Locale?.DateOrder ?? inherited.Locale?.DateOrder),
            Review = Review is null && inherited.Review is null ? null : new SupplierReviewSettings(Review?.Mode ?? inherited.Review?.Mode, Review?.AutoApproveMinConfidence ?? inherited.Review?.AutoApproveMinConfidence),
            Defaults = Defaults is null && inherited.Defaults is null ? null : new SupplierDefaults(Defaults?.Currency ?? inherited.Defaults?.Currency),
            Layouts = Layouts?.Required is null or InheritableSwitch.Inherit ? inherited.Layouts ?? Layouts : Layouts,
            RequireSenderAuth = RequireSenderAuth ?? inherited.RequireSenderAuth,
        };
    }

    /// <summary>Whether the supplier is layout-managed before its first layout exists (docs/11 §6.4): its own switch, else the tenant default.</summary>
    public bool LayoutsRequired(bool tenantDefault) => Layouts?.Required switch
    {
        InheritableSwitch.On => true,
        InheritableSwitch.Off => false,
        _ => tenantDefault,
    };

    /// <summary>Whether every document of the supplier goes to review.</summary>
    [JsonIgnore]
    public bool AlwaysReview => Review?.Mode == SupplierReviewMode.Always;

    /// <summary>
    /// The type the supplier configuration pins at ingest (docs/11 §7.3), or null: its single allowed type under
    /// <c>always</c>, or under <c>whenSourceMatched</c> (the default) when <paramref name="sourceMatched"/>.
    /// </summary>
    public string? PinnedType(bool sourceMatched)
    {
        if (AllowedTypeKeys is not { Count: 1 } allowed)
        {
            return null;
        }

        return (FixDocumentType ?? Suppliers.FixDocumentType.WhenSourceMatched) switch
        {
            Suppliers.FixDocumentType.Always => allowed[0],
            Suppliers.FixDocumentType.WhenSourceMatched when sourceMatched => allowed[0],
            _ => null,
        };
    }
}
