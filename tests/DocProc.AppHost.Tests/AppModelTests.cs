using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.AppHost.Tests;

/// <summary>
/// Builds the application model without starting anything, so these checks need neither Docker nor the Aspire
/// orchestrator. They pin the contract between the AppHost and the hosts: resource names are connection-string
/// names, and start-up order follows the migration ownership (API migrates, worker waits).
/// </summary>
public class AppModelTests
{
    private static async Task<DistributedApplicationModel> BuildModelAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.DocProc_AppHost>(TestContext.Current.CancellationToken);
        await using var app = await builder.BuildAsync(TestContext.Current.CancellationToken);
        return app.Services.GetRequiredService<DistributedApplicationModel>();
    }

    [Fact]
    public async Task Declares_every_local_dependency_from_docs_09()
    {
        var model = await BuildModelAsync();

        var names = model.Resources.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[] { "sql", "DocProc", "storage", "blobs", "messaging", "keycloak", "vault", "mail", "api", "worker" })
        {
            names.ShouldContain(expected);
        }
    }

    [Fact]
    public async Task Sql_server_runs_the_2025_image_and_the_database_is_the_connection_string_the_hosts_read()
    {
        var model = await BuildModelAsync();

        var sql = model.Resources.Single(r => r.Name == "sql");
        var image = sql.Annotations.OfType<ContainerImageAnnotation>().Single();
        image.Registry.ShouldBe("mcr.microsoft.com");
        image.Image.ShouldBe("mssql/server");
        image.Tag.ShouldBe("2025-latest");

        var database = model.Resources.Single(r => r.Name == "DocProc");
        database.ShouldBeAssignableTo<IResourceWithConnectionString>();
        database.Name.ShouldBe("DocProc"); // PersistenceHostExtensions.ConnectionStringName
    }

    [Fact]
    public async Task Pipeline_queues_exist_one_per_stage()
    {
        var model = await BuildModelAsync();

        var queues = model.Resources.Where(r => r.GetType().Name == "AzureServiceBusQueueResource").Select(r => r.Name).ToList();
        queues.ShouldBe(["ingest", "normalize", "split", "classify", "match", "extract", "validate", "publish", "synthesize-profile"], ignoreOrder: true);
    }

    [Fact]
    public async Task Api_waits_for_its_dependencies_and_the_worker_waits_for_the_api()
    {
        var model = await BuildModelAsync();

        var api = model.Resources.Single(r => r.Name == "api");
        var apiWaits = api.Annotations.OfType<WaitAnnotation>().Select(w => w.Resource.Name).ToList();
        foreach (var expected in new[] { "DocProc", "storage", "messaging", "keycloak" })
        {
            apiWaits.ShouldContain(expected);
        }
        api.Annotations.OfType<HealthCheckAnnotation>().ShouldNotBeEmpty();

        var worker = model.Resources.Single(r => r.Name == "worker");
        worker.Annotations.OfType<WaitAnnotation>().Select(w => w.Resource.Name).ShouldContain("api");
    }

    [Fact]
    public async Task Stateful_containers_are_persistent_with_named_volumes()
    {
        var model = await BuildModelAsync();

        foreach (var name in new[] { "sql", "keycloak" })
        {
            var resource = model.Resources.Single(r => r.Name == name);
            resource.Annotations.OfType<ContainerLifetimeAnnotation>().Single().Lifetime.ShouldBe(ContainerLifetime.Persistent);
            resource.Annotations.OfType<ContainerMountAnnotation>().ShouldContain(m => m.Type == ContainerMountType.Volume && m.Source!.StartsWith("docproc-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Keycloak_realm_import_seeds_platform_roles_two_tenants_and_the_api_audience()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "realms", "docproc-realm.json");
        File.Exists(path).ShouldBeTrue($"realm file not copied to output: {path}");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var realm = doc.RootElement;
        realm.GetProperty("realm").GetString().ShouldBe("docproc");
        realm.GetProperty("roles").GetProperty("realm").EnumerateArray().Select(r => r.GetProperty("name").GetString())
            .ShouldBe(["SuperAdmin", "Operator", "TenantAdmin", "Reviewer", "Viewer"], ignoreOrder: true);
        realm.GetProperty("clients").EnumerateArray().Select(c => c.GetProperty("clientId").GetString())
            .ShouldBe(["docproc-api", "docproc-portal", "docproc-admin"], ignoreOrder: true);

        var tenants = realm.GetProperty("users").EnumerateArray()
            .Where(u => u.TryGetProperty("attributes", out var a) && a.TryGetProperty("tenant_id", out _))
            .Select(u => u.GetProperty("attributes").GetProperty("tenant_id")[0].GetString())
            .Distinct()
            .ToList();
        tenants.Count.ShouldBe(2);
    }
}
