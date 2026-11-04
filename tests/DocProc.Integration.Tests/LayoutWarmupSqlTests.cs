using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Integration.Tests;

/// <summary>
/// The warm-up places of a layout version on SQL Server behind row-level security (docs/11 §8.3, §11): documents
/// validated at once, each in its own tenant session as parallel workers run them, take the places 1 to
/// <c>ReviewWarmup</c> exactly once each, none twice and none over the warm-up.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LayoutWarmupSqlTests(SqlServerFixture sqlServer) : IClassFixture<SqlServerFixture>
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private string _connectionString = string.Empty;

    [Fact]
    public async Task Documents_validated_at_once_take_each_warm_up_place_once()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        _connectionString = sqlServer.NewDatabaseConnectionString();
        var now = DateTimeOffset.UtcNow;
        await using (var platform = NewContext(TenantContext.Platform()))
        {
            await platform.Database.MigrateAsync(ct);
            platform.Tenants.Add(new Tenant { Id = TenantA, Name = "tenant-a", Slug = "tenant-a", LegalName = "tenant-a", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
            await platform.SaveChangesAsync(ct);
        }

        var versionId = Guid.CreateVersion7();
        await using (var seeding = NewContext(TenantContext.ForTenant(TenantA)))
        {
            var supplier = new Supplier { Id = Guid.CreateVersion7(), TenantId = TenantA, Name = "Nordwind", NormalizedName = "NORDWIND", CreatedAt = now, UpdatedAt = now };
            var layout = new SupplierLayout { Id = Guid.CreateVersion7(), TenantId = TenantA, Scope = SupplierLayoutScope.Supplier, SupplierId = supplier.Id, OwnerId = supplier.Id, DocumentTypeKey = "Invoice", LayoutKey = "nordwind-invoice-webshop", Name = "Nordwind web shop", ItemKinds = ["pdf"], ActiveVersion = 1, CreatedAt = now, UpdatedAt = now };
            seeding.Suppliers.Add(supplier);
            seeding.SupplierLayouts.Add(layout);
            seeding.SupplierLayoutVersions.Add(new SupplierLayoutVersion { Id = versionId, TenantId = TenantA, LayoutId = layout.Id, Version = 1, Status = SupplierLayoutVersionStatus.Active, IdentificationJson = """{"all":[{"text":"Rechnung"}]}""", IdentificationHash = "abcdef01", MappingJson = """{"fields":{}}""", CoverageJson = "[]", ReviewWarmup = 5, CreatedAt = now });
            await seeding.SaveChangesAsync(ct);
        }

        // 24 workers at once, each in its own tenant session; a semaphore lines them up before the first read.
        using var start = new SemaphoreSlim(0);
        var workers = Enumerable.Range(0, 24).Select(_ => Task.Run(
            async () =>
            {
                await using var session = NewContext(TenantContext.ForTenant(TenantA));
                await session.Database.OpenConnectionAsync(ct);
                await start.WaitAsync(ct);
                return await LayoutWarmup.TakePlaceAsync(session, versionId, ct);
            },
            ct)).ToList();
        start.Release(workers.Count);
        var places = await Task.WhenAll(workers);

        places.OfType<int>().Order().ToList().ShouldBe([1, 2, 3, 4, 5], "each place is taken once, by one worker");
        places.Count(p => p is null).ShouldBe(19, "the rest are past the warm-up");
        await using var check = NewContext(TenantContext.ForTenant(TenantA));
        (await check.SupplierLayoutVersions.AsNoTracking().SingleAsync(v => v.Id == versionId, ct)).WarmupSeen.ShouldBe(5);
        (await LayoutWarmup.TakePlaceAsync(check, versionId, ct)).ShouldBeNull();
    }

    private DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), _connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }
}
