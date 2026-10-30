using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocProc.Pipeline.Demo;

/// <summary>Host wiring for the demo data (docs/09 "Demo data").</summary>
public static class DemoDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DemoDataHostedService"/> when <c>Persistence:SeedDemoDocuments</c> is true and the development
    /// seed runs (SQLite, or <c>Persistence:SeedDevData</c> on SQL Server): without the seed there are no demo tenants
    /// to ingest for, which is the case under the AppHost. Refused in production like the other development switches.
    /// Call it after <c>AddDocProcProcessingStack</c> and after persistence, so it starts after the seed and the queue
    /// is the one the runner reads. Only the API calls it: one host ingests the demo documents, the worker processes them.
    /// </summary>
    public static IHostApplicationBuilder AddDocProcDemoData(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = PersistenceSettings.From(builder.Configuration);
        if (!settings.SeedDemoDocuments)
        {
            return builder;
        }

        if (builder.Environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{PersistenceSettings.SectionName}:SeedDemoDocuments is development-only and cannot be used when the environment is Production. Set {PersistenceSettings.SectionName}:SeedDemoDocuments=false.");
        }

        var provider = settings.Resolve(builder.Configuration.GetConnectionString(PersistenceHostExtensions.ConnectionStringName));
        if (!settings.AppliesDevSeed(provider))
        {
            return builder;
        }

        var path = string.IsNullOrWhiteSpace(settings.DemoDocumentsPath) ? PersistenceSettings.DefaultDemoDocumentsPath : settings.DemoDocumentsPath;
        builder.Services.AddSingleton(new DemoDataLocation(RepositoryPaths.ResolveUpwards(path, builder.Environment.ContentRootPath)));
        builder.Services.AddHostedService<DemoDataHostedService>();
        return builder;
    }
}
