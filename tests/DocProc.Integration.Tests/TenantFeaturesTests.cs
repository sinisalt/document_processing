using DocProc.Application.Configuration;
using DocProc.Infrastructure.Configuration;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Integration.Tests;

/// <summary>
/// Per-tenant feature flags (docs/11 §3, principle 6): stored in <see cref="TenantSettings.Features"/>, all off by
/// default, read only through <see cref="ITenantFeatures"/>. Runs on SQLite (the JSON column works the same way);
/// <see cref="P0SeamsMigrationTests"/> covers a settings row written before the flags existed on SQL Server.
/// </summary>
public sealed class TenantFeaturesTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"docproc-features-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Every_flag_is_off_by_default()
    {
        var flags = new TenantSettings().Features.ShouldNotBeNull();

        EfTenantFeatures.From(flags).ShouldBeSameAs(TenantFeatureSet.None);
        EfTenantFeatures.From(null).ShouldBeSameAs(TenantFeatureSet.None);
        foreach (var feature in Enum.GetValues<TenantFeature>())
        {
            TenantFeatureSet.None.IsEnabled(feature).ShouldBeFalse();
        }
    }

    [Fact]
    public void Each_stored_flag_maps_to_its_feature()
    {
        // One flag per feature, named as docs/11 §3 names them; a new feature without a flag fails here.
        var names = typeof(TenantFeatureFlags).GetProperties().Select(p => p.Name).ToList();
        names.ShouldBe(Enum.GetNames<TenantFeature>(), ignoreOrder: true);

        foreach (var property in typeof(TenantFeatureFlags).GetProperties())
        {
            var flags = new TenantFeatureFlags();
            property.SetValue(flags, true);
            EfTenantFeatures.From(flags).Enabled.ShouldBe([Enum.Parse<TenantFeature>(property.Name)]);
        }
    }

    [Fact]
    public async Task Flags_are_stored_in_the_settings_json_and_read_through_the_service()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        await using (var platform = NewContext(TenantContext.Platform()))
        {
            // A new tenant stores the flags object (all off), so it is tracked and edited in place.
            var tenant = await platform.Tenants.SingleAsync(t => t.Id == TenantA, ct);
            tenant.Settings.Features.ShouldNotBeNull().ConfigurableTypes = true;
            await platform.SaveChangesAsync(ct);
        }

        await using (var platform = NewContext(TenantContext.Platform()))
        {
            var tenant = await platform.Tenants.SingleAsync(t => t.Id == TenantA, ct);
            tenant.Settings.Features.ShouldNotBeNull().ConfigurableTypes.ShouldBeTrue();
            tenant.Settings.Features.Outputs = true;
            await platform.SaveChangesAsync(ct);
        }

        await using (var asA = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var features = new EfTenantFeatures(asA);
            (await features.GetAsync(TenantA, ct)).Enabled.ShouldBe([TenantFeature.ConfigurableTypes, TenantFeature.Outputs], ignoreOrder: true);
            (await features.IsEnabledAsync(TenantA, TenantFeature.SupplierLayouts, ct)).ShouldBeFalse();

            // Another tenant's switches are not visible from a tenant scope: they read as all off.
            (await features.GetAsync(TenantB, ct)).ShouldBeSameAs(TenantFeatureSet.None);
        }

        await using var asPlatform = NewContext(TenantContext.Platform());
        (await new EfTenantFeatures(asPlatform).GetAsync(TenantB, ct)).ShouldBeSameAs(TenantFeatureSet.None);
        (await new EfTenantFeatures(asPlatform).IsEnabledAsync(TenantA, TenantFeature.Outputs, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Settings_stored_before_the_flags_existed_read_as_all_off()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await using (var raw = new SqliteConnection($"Data Source={_path}"))
        {
            await raw.OpenAsync(ct);
            await using var update = raw.CreateCommand();
            update.CommandText = "UPDATE Tenants SET Settings = json_remove(Settings, '$.Features')";
            (await update.ExecuteNonQueryAsync(ct)).ShouldBe(2);
        }

        await using var asA = NewContext(TenantContext.ForTenant(TenantA));
        (await new EfTenantFeatures(asA).GetAsync(TenantA, ct)).ShouldBeSameAs(TenantFeatureSet.None);
        var tenant = await asA.Tenants.AsNoTracking().SingleAsync(ct);
        tenant.Settings.RetentionYears.ShouldBe(10);
    }

    [Fact]
    public void Both_persistence_providers_register_the_service()
    {
        var services = new ServiceCollection();
        services.AddDocProcSqlitePersistence(_path, Path.GetTempPath());
        services.ShouldContain(d => d.ServiceType == typeof(ITenantFeatures) && d.ImplementationType == typeof(EfTenantFeatures) && d.Lifetime == ServiceLifetime.Scoped);

        var sqlServer = new ServiceCollection();
        sqlServer.AddDocProcPersistence("Server=unused;Database=unused");
        sqlServer.ShouldContain(d => d.ServiceType == typeof(ITenantFeatures) && d.ImplementationType == typeof(EfTenantFeatures));
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        await using var platform = NewContext(TenantContext.Platform());
        await platform.Database.EnsureCreatedAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, slug) in new[] { (TenantA, "tenant-a"), (TenantB, "tenant-b") })
        {
            platform.Tenants.Add(new Tenant { Id = id, Name = slug, Slug = slug, LegalName = slug, DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
        }

        await platform.SaveChangesAsync(ct);
    }

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlite(new DbContextOptionsBuilder<DocProcDbContext>(), $"Data Source={_path}");
        return new DocProcDbContext(options.Options, tenant);
    }
}
