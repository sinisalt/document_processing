using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Infrastructure.Persistence;

public static class PersistenceOptions
{
    /// <summary>SQL Server provider settings shared by the hosts, the design-time factory and the integration tests.</summary>
    public static DbContextOptionsBuilder<DocProcDbContext> ConfigureSqlServer(
        DbContextOptionsBuilder<DocProcDbContext> builder,
        string connectionString,
        bool migrationsOnly = false,
        ITenantContext? tenant = null)
    {
        builder.UseSqlServer(connectionString, sql =>
        {
            sql.UseCompatibilityLevel(DocProcDbContext.CompatibilityLevel);
            sql.MigrationsAssembly(typeof(DocProcDbContext).Assembly.FullName);
            sql.EnableRetryOnFailure(maxRetryCount: 5);
        });

        if (!migrationsOnly && tenant is not null)
        {
            builder.AddInterceptors(new TenantSessionContextInterceptor(tenant));
        }

        return builder;
    }

    /// <summary>Registers the context with a scoped <see cref="ITenantContext"/>, the session-context interceptor and the connection string.</summary>
    public static IServiceCollection AddDocProcPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddDbContext<DocProcDbContext>((sp, options) =>
        {
            var tenant = sp.GetRequiredService<ITenantContext>();
            ConfigureSqlServer((DbContextOptionsBuilder<DocProcDbContext>)options, connectionString, tenant: tenant);
        });
        return services;
    }
}

/// <summary>Host wiring shared by the API and the worker.</summary>
public static class PersistenceHostExtensions
{
    public const string ConnectionStringName = "DocProc";
    public const string MigrateOnStartupKey = "Persistence:MigrateOnStartup";

    /// <summary>
    /// Registers persistence when <c>ConnectionStrings:DocProc</c> is configured, and the start-up migration when
    /// <c>Persistence:MigrateOnStartup</c> is true. Returns false when no connection string is present so a host can
    /// still start for smoke tests without a database.
    /// </summary>
    public static bool TryAddDocProcPersistence(this IServiceCollection services, IConfiguration configuration, bool migrateByDefault)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        services.AddDocProcPersistence(connectionString);
        if (configuration.GetValue(MigrateOnStartupKey, migrateByDefault))
        {
            services.AddHostedService<MigrationHostedService>();
        }

        return true;
    }
}
