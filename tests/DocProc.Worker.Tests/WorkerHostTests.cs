using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Llm.Providers;
using DocProc.Pipeline.Queue;
using DocProc.Pipeline.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocProc.Worker.Tests;

/// <summary>
/// The worker host as <c>Program.cs</c> composes it (docs/09 "Running without Docker"): booted on SQLite with the
/// scripted LLM provider, or merely built in a production-like configuration. The worker has no authentication
/// surface, so of the API's three refused development switches only the two persistence ones apply here.
/// </summary>
public class WorkerHostTests
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Hosted_services_start_and_stop_cleanly_on_sqlite()
    {
        var directory = WorkerTestHost.CreateDirectory();
        var host = await WorkerTestHost.StartAsync(directory);
        try
        {
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStarted.IsCancellationRequested.ShouldBeTrue("every IHostedService completed StartAsync");

            var hosted = host.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();
            hosted.ShouldContain(typeof(PipelineRunnerHostedService), "the worker exists to run stages");
            hosted.ShouldContain(typeof(DevSeedHostedService), "the SQLite mode implies the development seed");
            hosted.ShouldNotContain(typeof(MigrationHostedService), "the API owns schema migration; the worker only connects");

            host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.ShouldBe("Testing");
            host.Services.GetRequiredService<IConfiguration>()["Pipeline:MaxArchiveDepth"].ShouldBe("2", "the worker's appsettings.json is read from the content root");
            host.Services.GetRequiredService<SqliteDatabaseLocation>().Path.ShouldBe(Path.Combine(directory, "docproc.db"));
            host.Services.GetRequiredService<ScriptedLlmProvider>().ShouldNotBeNull("Llm:Replay:Scripted=true registers the scripted provider");

            await using (var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
            {
                (await db.Tenants.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2, "the seed ran before the host reported started");
            }

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await host.StopAsync(stop.Token);
            lifetime.ApplicationStopped.IsCancellationRequested.ShouldBeTrue();
        }
        finally
        {
            await WorkerTestHost.StopAndCleanAsync(host, directory);
        }
    }

    [Fact]
    public async Task Runner_consumes_an_enqueued_ingestion_and_settles_the_document()
    {
        var directory = WorkerTestHost.CreateDirectory();
        var host = await WorkerTestHost.StartAsync(directory);
        try
        {
            var ingestion = await WorkerTestHost.SeedIngestionAsync(host.Services, DevSeed.TenantA, "rechnung.xml", "application/xml", WorkerTestHost.Fixture(WorkerTestHost.UblFixture));

            await host.Services.GetRequiredService<IPipelineQueue>().EnqueueAsync(new ProcessIngestion(DevSeed.TenantA, ingestion.Id), TestContext.Current.CancellationToken);

            using var settle = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            settle.CancelAfter(SettleTimeout);
            await host.Services.GetRequiredService<InProcessPipelineQueue>().WaitForIdleAsync(settle.Token);

            await using var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
            var document = await db.Documents.AsNoTracking()
                .Include(d => d.IngestionItem)
                .Where(d => d.IngestionItem!.IngestionId == ingestion.Id)
                .SingleAsync(TestContext.Current.CancellationToken);

            document.FailureReason.ShouldBeNull();
            document.Status.ShouldBe(DocumentStatus.AutoApproved, "the structured e-invoice path needs no LLM and no supplier (SUPPLIER_UNMATCHED is a warning)");
            document.Type.ShouldBe(DocumentType.Invoice);
            document.ExtractionMethod.ShouldBe(ExtractionMethod.StructuredXml);
            document.DocumentNumber.ShouldBe("RE-2026-10412");
        }
        finally
        {
            await WorkerTestHost.StopAndCleanAsync(host, directory);
        }
    }

    [Fact]
    public void Production_host_with_sql_server_passes_validate_on_build_without_connecting()
    {
        // Built, never started: nothing may open a connection while the service provider is validated.
        var builder = WorkerHost.CreateBuilder(WorkerTestHost.SqlServerArgs("Production"));
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        using var host = Should.NotThrow(builder.Build);

        host.Services.GetRequiredService<IHostEnvironment>().IsProduction().ShouldBeTrue();
        var hosted = host.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();
        hosted.ShouldContain(typeof(PipelineRunnerHostedService), "the worker runs stages everywhere unless Pipeline:RunInProcess says otherwise");
        hosted.ShouldNotContain(typeof(DevSeedHostedService));
        hosted.ShouldNotContain(typeof(MigrationHostedService));
        host.Services.GetRequiredService<ISystemDbContextFactory>().ShouldNotBeNull("persistence is registered against the configured SQL Server");
    }

    [Fact]
    public void Production_host_without_the_runner_still_passes_validate_on_build()
    {
        var builder = WorkerHost.CreateBuilder(WorkerTestHost.SqlServerArgs("Production", ("Pipeline:RunInProcess", "false")));
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        using var host = Should.NotThrow(builder.Build);

        host.Services.GetServices<IHostedService>().Select(s => s.GetType()).ShouldNotContain(typeof(PipelineRunnerHostedService));
        host.Services.GetRequiredService<IPipelineQueue>().ShouldNotBeNull("the queue stays so the host can enqueue");
    }

    [Fact]
    public void Seed_dev_data_in_production_refuses_to_start()
    {
        var error = Should.Throw<InvalidOperationException>(() => WorkerHost.CreateBuilder(WorkerTestHost.SqlServerArgs("Production", ("Persistence:SeedDevData", "true"))));

        error.Message.ShouldContain("Production");
        error.Message.ShouldContain("SeedDevData");
    }

    [Fact]
    public void Sqlite_in_production_refuses_to_start()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docproc-worker-tests", "never-created");

        var error = Should.Throw<InvalidOperationException>(() => WorkerHost.CreateBuilder(WorkerTestHost.SqliteArgs(directory, environment: "Production")));

        error.Message.ShouldContain("Production");
        error.Message.ShouldContain("Sqlite");
        Directory.Exists(directory).ShouldBeFalse();
    }
}
