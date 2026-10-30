using DocProc.Application.Security;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    /// <summary>The scoped tenant context, set per request by the API's tenant resolution and per job by the worker.</summary>
    public static IServiceCollection AddTenantContext(this IServiceCollection services)
    {
        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        return services;
    }

    /// <summary>Registers the context with a scoped <see cref="ITenantContext"/>, the session-context interceptor and the connection string.</summary>
    public static IServiceCollection AddDocProcPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddTenantContext();
        services.AddDbContext<DocProcDbContext>((sp, options) =>
        {
            var tenant = sp.GetRequiredService<ITenantContext>();
            ConfigureSqlServer((DbContextOptionsBuilder<DocProcDbContext>)options, connectionString, tenant: tenant);
        });
        services.AddSingleton<ISystemDbContextFactory>(new SqlServerSystemDbContextFactory(connectionString));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IApiKeyValidator, ApiKeyValidator>();
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
