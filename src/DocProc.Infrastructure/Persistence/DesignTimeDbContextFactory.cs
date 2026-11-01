using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocProc.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without a database or a running host.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DocProcDbContext>
{
    public DocProcDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DocProcDbContext>();
        PersistenceOptions.ConfigureSqlServer(options, "Server=localhost;Database=DocProc;Integrated Security=false;TrustServerCertificate=true", migrationsOnly: true);
        return new DocProcDbContext(options.Options, TenantContext.Platform());
    }
}
