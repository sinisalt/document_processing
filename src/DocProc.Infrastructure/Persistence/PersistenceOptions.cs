using DocProc.Application.Security;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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

    /// <summary>
    /// SQLite provider settings for the development mode (docs/09 "Running without Docker"). No session-context
    /// interceptor: SQLite has no SESSION_CONTEXT and no row-level security, so the EF query filters are the only
    /// tenancy layer here.
    /// </summary>
    public static DbContextOptionsBuilder<DocProcDbContext> ConfigureSqlite(DbContextOptionsBuilder<DocProcDbContext> builder, string connectionString)
    {
        builder.UseSqlite(connectionString);
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

    /// <summary>
    /// Registers the context against a SQLite file, the platform-scoped factory, the API key validator and the
    /// start-up schema creation (<see cref="SqliteSchemaHostedService"/>). A relative <paramref name="databasePath"/>
    /// is anchored to <paramref name="contentRootPath"/> (the project folder under <c>dotnet run</c>), not to the
    /// process working directory, so an IDE or CI run finds the same file. The file's folder is created here so the
    /// first run on a clean checkout works.
    /// </summary>
    public static IServiceCollection AddDocProcSqlitePersistence(this IServiceCollection services, string databasePath, string contentRootPath)
    {
        var fullPath = Path.GetFullPath(databasePath, Path.GetFullPath(contentRootPath));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var location = new SqliteDatabaseLocation(fullPath);
        services.AddSingleton(location);
        services.AddTenantContext();
        services.AddDbContext<DocProcDbContext>(options => ConfigureSqlite((DbContextOptionsBuilder<DocProcDbContext>)options, location.ConnectionString));
        services.AddSingleton<ISystemDbContextFactory>(new SqliteSystemDbContextFactory(location.ConnectionString));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IApiKeyValidator, ApiKeyValidator>();
        services.AddHostedService<SqliteSchemaHostedService>();
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

    /// <summary>
    /// Provider-aware variant (docs/09 "Running without Docker"): <c>Persistence:Provider</c> selects SQL Server
    /// (the default whenever a connection string exists; behaves exactly like the overload above) or SQLite
    /// (<c>Persistence:SqlitePath</c>, schema from the model, refused in production). The development seed
    /// (<see cref="DevSeed"/>) runs after the schema step when the provider is SQLite or
    /// <c>Persistence:SeedDevData</c> is true; like the other development switches the seed is refused in
    /// production, because its tenants have fixed, publicly known ids. Returns false when no provider applies.
    /// </summary>
    public static bool TryAddDocProcPersistence(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, bool migrateByDefault)
    {
        var settings = PersistenceSettings.From(configuration);
        var provider = settings.Resolve(configuration.GetConnectionString(ConnectionStringName));
        if (settings.SeedDevData && environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{PersistenceSettings.SectionName}:SeedDevData is development-only (it inserts the demo tenants with fixed, publicly known ids) and cannot be used when the environment is Production. Set {PersistenceSettings.SectionName}:SeedDevData=false.");
        }

        if (settings.SeedDemoDocuments && environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{PersistenceSettings.SectionName}:SeedDemoDocuments is development-only (it ingests the demo invoices of fixtures/demo for the demo tenants and seeds members and LLM usage for the development users) and cannot be used when the environment is Production. Set {PersistenceSettings.SectionName}:SeedDemoDocuments=false.");
        }

        switch (provider)
        {
            case null:
                return false;

            case PersistenceProvider.SqlServer:
                if (!services.TryAddDocProcPersistence(configuration, migrateByDefault))
                {
                    throw new InvalidOperationException($"{PersistenceSettings.SectionName}:Provider is SqlServer but ConnectionStrings:{ConnectionStringName} is not configured.");
                }

                break;

            case PersistenceProvider.Sqlite:
                if (environment.IsProduction())
                {
                    throw new InvalidOperationException(
                        $"{PersistenceSettings.SectionName}:Provider=Sqlite is a development-only mode (no migrations, no row-level security, no temporal tables) and cannot be used when the environment is Production. Configure ConnectionStrings:{ConnectionStringName} and Persistence:Provider=SqlServer instead.");
                }

                services.AddDocProcSqlitePersistence(string.IsNullOrWhiteSpace(settings.SqlitePath) ? PersistenceSettings.DefaultSqlitePath : settings.SqlitePath, environment.ContentRootPath);
                break;

            default:
                throw new InvalidOperationException($"Unknown persistence provider '{provider}'.");
        }

        if (provider == PersistenceProvider.Sqlite || settings.SeedDevData)
        {
            services.AddHostedService<DevSeedHostedService>();
        }

        return true;
    }
}
