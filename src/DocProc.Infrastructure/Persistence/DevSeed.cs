using DocProc.Domain.Common;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Providers;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// Demo data for the development mode (docs/09 "Running without Docker"): the two tenants of the Keycloak realm in
/// <c>src/DocProc.AppHost/realms/docproc-realm.json</c>, an API-upload channel and a few suppliers each, the platform
/// Anthropic provider (disabled until a key is stored under its secret reference) and tenant A's LLM policy. Every
/// row has a fixed id, so applying the seed twice changes nothing and hand-made changes to other rows are kept.
/// </summary>
public static class DevSeed
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public static readonly Guid TenantAUploadChannel = Guid.Parse("aaaaaaaa-0001-4000-8000-000000000001");
    public static readonly Guid TenantBUploadChannel = Guid.Parse("bbbbbbbb-0001-4000-8000-000000000001");

    public static readonly Guid MetroDeutschland = Guid.Parse("aaaaaaaa-0002-4000-8000-000000000001");
    public static readonly Guid GetraenkeHoffmann = Guid.Parse("aaaaaaaa-0002-4000-8000-000000000002");
    public static readonly Guid BioHofMueller = Guid.Parse("aaaaaaaa-0002-4000-8000-000000000003");
    public static readonly Guid KonzumPlus = Guid.Parse("bbbbbbbb-0002-4000-8000-000000000001");
    public static readonly Guid PekaraDubravica = Guid.Parse("bbbbbbbb-0002-4000-8000-000000000002");

    /// <summary>The platform-scoped Anthropic configuration (docs/08 "Provider configuration model"); its key lives under <see cref="PlatformSecretRef"/>.</summary>
    public static readonly Guid PlatformAnthropicConfig = Guid.Parse("00000000-0003-4000-8000-000000000001");
    public const string PlatformSecretRef = "llm-platform-eu";

    /// <summary>Models per stage from docs/06 "Model choice": Haiku for the small, frequent calls; Opus for extraction and profile synthesis.</summary>
    public static readonly IReadOnlyDictionary<string, string> PlatformModels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(LlmStage.Classify)] = "claude-haiku-5-5",
        [nameof(LlmStage.Split)] = "claude-haiku-5-5",
        [nameof(LlmStage.Extract)] = "claude-opus-5-5",
        [nameof(LlmStage.Synthesize)] = "claude-opus-5-5",
    };

    /// <summary>
    /// The <c>sub</c> the development identity provider issues for a user (a GUID in Keycloak's shape, derived from the
    /// username so it never changes); <c>DocProc.Api.Dev.DevUsers</c> and the demo members (docs/09 "Demo data") share it.
    /// </summary>
    public static string SubjectFor(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("docproc-dev:" + username.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    /// <summary>Inserts every missing row and returns how many were inserted. Must run in platform scope.</summary>
    public static async Task<int> ApplyAsync(DocProcDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!db.Tenant.IsPlatformScope)
        {
            throw new InvalidOperationException("The development seed must run in platform scope.");
        }

        var now = clock.GetUtcNow();
        var inserted = 0;

        inserted += await AddMissingAsync(db.Tenants, t => t.Id == TenantA, () => new Tenant
        {
            Id = TenantA,
            Name = "Gasthaus zur Post GmbH",
            Slug = "tenant-a",
            LegalName = "Gasthaus zur Post GmbH",
            VatId = "DE123456789",
            NameAliases = ["Gasthaus zur Post"],
            CountryCode = "DE",
            DefaultCurrency = "EUR",
            TimeZone = "Europe/Berlin",
            Settings = new TenantSettings { RetentionYears = 8 },
            CreatedAt = now,
            UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        inserted += await AddMissingAsync(db.Tenants, t => t.Id == TenantB, () => new Tenant
        {
            Id = TenantB,
            Name = "Pizzeria Napoli d.o.o.",
            Slug = "tenant-b",
            LegalName = "Pizzeria Napoli d.o.o.",
            VatId = "HR12345678901",
            NameAliases = ["Pizzeria Napoli"],
            CountryCode = "HR",
            DefaultCurrency = "EUR",
            TimeZone = "Europe/Zagreb",
            Settings = new TenantSettings { RetentionYears = 11 },
            CreatedAt = now,
            UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        inserted += await AddMissingAsync(db.IngestionChannels, c => c.Id == TenantAUploadChannel, () => UploadChannel(TenantAUploadChannel, TenantA, now), cancellationToken).ConfigureAwait(false);
        inserted += await AddMissingAsync(db.IngestionChannels, c => c.Id == TenantBUploadChannel, () => UploadChannel(TenantBUploadChannel, TenantB, now), cancellationToken).ConfigureAwait(false);

        inserted += await AddMissingAsync(db.Suppliers, s => s.Id == MetroDeutschland, () => Supplier(MetroDeutschland, TenantA, "METRO Deutschland GmbH", "DE811177936", "DE", ["metro.de"], now), cancellationToken).ConfigureAwait(false);
        inserted += await AddMissingAsync(db.Suppliers, s => s.Id == GetraenkeHoffmann, () => Supplier(GetraenkeHoffmann, TenantA, "Getränke Hoffmann GmbH", null, "DE", ["getraenke-hoffmann.de"], now), cancellationToken).ConfigureAwait(false);
        inserted += await AddMissingAsync(db.Suppliers, s => s.Id == BioHofMueller, () => Supplier(BioHofMueller, TenantA, "Bio-Hof Müller", null, "DE", [], now), cancellationToken).ConfigureAwait(false);
        inserted += await AddMissingAsync(db.Suppliers, s => s.Id == KonzumPlus, () => Supplier(KonzumPlus, TenantB, "Konzum plus d.o.o.", "HR62226620908", "HR", ["konzum.hr"], now), cancellationToken).ConfigureAwait(false);
        inserted += await AddMissingAsync(db.Suppliers, s => s.Id == PekaraDubravica, () => Supplier(PekaraDubravica, TenantB, "Pekara Dubravica d.o.o.", null, "HR", ["dubravica.hr"], now), cancellationToken).ConfigureAwait(false);

        inserted += await AddMissingAsync(db.LlmProviderConfigs, c => c.Id == PlatformAnthropicConfig, () => new LlmProviderConfigRecord
        {
            Id = PlatformAnthropicConfig,
            Scope = ConfigScope.Platform,
            TenantId = null,
            Kind = LlmProviderKind.Anthropic,
            AuthType = LlmAuthType.ApiKey,
            SecretRef = PlatformSecretRef,
            Models = new Dictionary<string, string>(PlatformModels, StringComparer.Ordinal),
            DataResidency = DataResidency.Eu,
            Status = ProviderConfigStatus.Disabled,
            CreatedBy = "dev-seed",
            CreatedAt = now,
            UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        // Tenant A runs on the platform key (no BYOK config); tenant B is left without a policy on purpose.
        inserted += await AddMissingAsync(db.TenantLlmPolicies, p => p.TenantId == TenantA, () => new TenantLlmPolicyRecord
        {
            TenantId = TenantA,
            DefaultConfigId = null,
            FallbackToPlatformKey = true,
            AllowPlatformKeyAtAll = true,
            UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        if (inserted > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return inserted;
    }

    private static IngestionChannel UploadChannel(Guid id, Guid tenantId, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = "API upload",
        Type = ChannelType.ApiUpload,
        Status = ChannelStatus.Active,
        CreatedAt = now,
    };

    private static Supplier Supplier(Guid id, Guid tenantId, string name, string? vatId, string countryCode, List<string> emailDomains, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = name,
        NormalizedName = Identifiers.NormalizeName(name),
        VatId = vatId,
        EmailDomains = emailDomains,
        DefaultCurrency = "EUR",
        AddressesJson = $$"""[{"country":"{{countryCode}}"}]""",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static async Task<int> AddMissingAsync<TEntity>(DbSet<TEntity> set, System.Linq.Expressions.Expression<Func<TEntity, bool>> exists, Func<TEntity> create, CancellationToken cancellationToken)
        where TEntity : class
    {
        if (await set.AnyAsync(exists, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        set.Add(create());
        return 1;
    }
}
