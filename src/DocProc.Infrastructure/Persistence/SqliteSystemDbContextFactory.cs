using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Persistence;

/// <summary>SQLite counterpart of the SQL Server factory for the development mode: no session context, EF query filters only.</summary>
internal sealed class SqliteSystemDbContextFactory(string connectionString) : ISystemDbContextFactory
{
    public DocProcDbContext CreatePlatformScoped()
    {
        var options = PersistenceOptions.ConfigureSqlite(new DbContextOptionsBuilder<DocProcDbContext>(), connectionString);
        return new DocProcDbContext(options.Options, TenantContext.Platform());
    }
}
