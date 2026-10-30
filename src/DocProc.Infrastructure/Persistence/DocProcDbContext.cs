using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// SQL Server context for the platform (docs/01 "Data"). Tenant-scoped entities get a global query filter from
/// <see cref="ITenantContext"/>; <see cref="TenantSessionContextInterceptor"/> and the row-level security policy
/// enforce the same boundary inside the database.
/// </summary>
public sealed class DocProcDbContext(DbContextOptions<DocProcDbContext> options, ITenantContext tenant) : DbContext(options)
{
    /// <summary>SQL Server 2025 / Azure SQL: native json type and vector support (docs/01).</summary>
    public const int CompatibilityLevel = 170;

    public ITenantContext Tenant { get; } = tenant;

    /// <summary>SQLite-only storage of timestamps (see <see cref="OnModelCreating"/>).</summary>
    private static readonly ValueConverter<DateTimeOffset, long> UtcTicks = new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMember> TenantMembers => Set<TenantMember>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<IngestionChannel> IngestionChannels => Set<IngestionChannel>();
    public DbSet<GlobalSupplier> GlobalSuppliers => Set<GlobalSupplier>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<ExtractorProfile> ExtractorProfiles => Set<ExtractorProfile>();
    public DbSet<Ingestion> Ingestions => Set<Ingestion>();
    public DbSet<IngestionItem> IngestionItems => Set<IngestionItem>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentFlagRecord> DocumentFlags => Set<DocumentFlagRecord>();
    public DbSet<DocumentEvent> DocumentEvents => Set<DocumentEvent>();
    public DbSet<ReviewTask> ReviewTasks => Set<ReviewTask>();
    public DbSet<Correction> Corrections => Set<Correction>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<LlmProviderConfigRecord> LlmProviderConfigs => Set<LlmProviderConfigRecord>();
    public DbSet<TenantLlmPolicyRecord> TenantLlmPolicies => Set<TenantLlmPolicyRecord>();
    public DbSet<LlmUsageEntry> LlmUsage => Set<LlmUsageEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DocProcDbContext).Assembly);

        var sqlite = Database.IsSqlite();
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // Enums as readable strings everywhere; the storage cost is negligible next to the json columns.
            foreach (var property in entityType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (type.IsEnum)
                {
                    property.SetProviderClrType(typeof(string));
                    property.SetMaxLength(64);
                }
                else if (sqlite && type == typeof(DateTimeOffset))
                {
                    // SQLite stores DateTimeOffset as text and cannot compare or order it in a query, which every
                    // keyset cursor and time window needs; the development mode stores UTC ticks instead. All
                    // timestamps are UTC (docs/02 "Conventions"), so nothing is lost. SQL Server keeps datetimeoffset.
                    property.SetValueConverter(UtcTicks);
                }
            }

            if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType) && entityType.ClrType != typeof(Tenant))
            {
                modelBuilder.Entity(entityType.ClrType).HasIndex(nameof(ITenantScoped.TenantId));
            }
        }

        // Query filters; the lambda captures this context so the filter follows the scoped tenant.
        modelBuilder.Entity<Tenant>().HasQueryFilter(t => Tenant.IsPlatformScope || t.Id == Tenant.TenantId);
        modelBuilder.Entity<TenantMember>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<ApiKey>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<IngestionChannel>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<Supplier>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<Ingestion>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<IngestionItem>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<Document>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<DocumentFlagRecord>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<DocumentEvent>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<ReviewTask>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<Correction>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<OutboxMessage>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<IdempotencyKey>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<TenantLlmPolicyRecord>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);
        modelBuilder.Entity<LlmUsageEntry>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == Tenant.TenantId);

        // Platform configs (TenantId null) are visible to every tenant's gateway call; tenant configs only to their tenant.
        modelBuilder.Entity<LlmProviderConfigRecord>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == null || e.TenantId == Tenant.TenantId);

        // Profiles are shared through the global registry unless private to a tenant.
        modelBuilder.Entity<ExtractorProfile>().HasQueryFilter(e => Tenant.IsPlatformScope || e.TenantId == null || e.TenantId == Tenant.TenantId);
    }
}
