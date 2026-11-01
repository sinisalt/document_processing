using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// Creates platform-scoped contexts for work that runs before or outside a tenant request: API key lookup, cross-tenant
/// jobs, the migration runner. Every use is a deliberate exception to the per-request tenant scope, so keep them few.
/// </summary>
public interface ISystemDbContextFactory
{
    DocProcDbContext CreatePlatformScoped();
}

internal sealed class SqlServerSystemDbContextFactory(string connectionString) : ISystemDbContextFactory
{
    public DocProcDbContext CreatePlatformScoped()
    {
        var tenant = TenantContext.Platform();
        var options = PersistenceOptions.ConfigureSqlServer(new DbContextOptionsBuilder<DocProcDbContext>(), connectionString, tenant: tenant);
        return new DocProcDbContext(options.Options, tenant);
    }
}
