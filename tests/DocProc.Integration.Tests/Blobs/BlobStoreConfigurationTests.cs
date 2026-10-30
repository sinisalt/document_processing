using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DocProc.Integration.Tests.Blobs;

/// <summary><c>AddDocProcBlobs</c>: provider selection and inference from configuration, and fail-fast validation at start-up.</summary>
public sealed class BlobStoreConfigurationTests : IDisposable
{
    private const string DevelopmentStorageKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Fact]
    public void Defaults_to_the_file_system_store_under_data_blobs_in_the_content_root()
    {
        using var provider = Build([], contentRoot: _temp);

        var store = provider.GetRequiredService<IBlobStore>();

        var fileSystem = store.ShouldBeOfType<FileSystemBlobStore>();
        fileSystem.RootPath.ShouldBe(Path.GetFullPath(Path.Combine(_temp, "data", "blobs")));
        provider.GetRequiredService<IContainerBlobStore>().ShouldBeSameAs(store);
        provider.GetRequiredService<IStartupValidator>().Validate();
        Directory.Exists(Path.Combine(_temp, "data", "blobs")).ShouldBeTrue();
        new BlobStoreOptions().RootPath.ShouldBe("./data/blobs");
    }

    [Fact]
    public void File_system_root_path_comes_from_configuration()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:Provider"] = "FileSystem", ["Blobs:RootPath"] = _temp });

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IBlobStore>().ShouldBeOfType<FileSystemBlobStore>().RootPath.ShouldBe(Path.GetFullPath(_temp));
        Directory.Exists(_temp).ShouldBeTrue();
    }

    [Fact]
    public void An_absolute_root_path_is_not_rebased_onto_the_content_root()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:RootPath"] = _temp }, contentRoot: Path.GetTempPath());

        provider.GetRequiredService<IBlobStore>().ShouldBeOfType<FileSystemBlobStore>().RootPath.ShouldBe(Path.GetFullPath(_temp));
    }

    [Fact]
    public void Azure_provider_uses_the_connection_string_the_app_host_injects()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Blobs:Provider"] = "Azure",
            ["ConnectionStrings:blobs"] = "UseDevelopmentStorage=true",
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IBlobStore>().ShouldBeOfType<AzureBlobStore>();
        provider.GetRequiredService<IOptions<BlobStoreOptions>>().Value.ConnectionString.ShouldBe("UseDevelopmentStorage=true");
    }

    [Fact]
    public void Connection_string_from_the_app_host_alone_selects_azure()
    {
        // The AppHost injects ConnectionStrings:blobs and nothing else: both hosts must land on Azurite, not on
        // their own ./data/blobs, or the worker cannot read what the API ingested.
        using var provider = Build(new Dictionary<string, string?> { ["ConnectionStrings:blobs"] = "UseDevelopmentStorage=true" }, contentRoot: _temp);

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IOptions<BlobStoreOptions>>().Value.Provider.ShouldBeNull();
        provider.GetRequiredService<IOptions<BlobStoreOptions>>().Value.EffectiveProvider.ShouldBe(BlobStoreProvider.Azure);
        provider.GetRequiredService<IBlobStore>().ShouldBeOfType<AzureBlobStore>();
        Directory.Exists(Path.Combine(_temp, "data")).ShouldBeFalse();
    }

    [Fact]
    public void Service_uri_through_configuration_selects_azure_with_a_token_credential()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:ServiceUri"] = "https://docprocprod.blob.core.windows.net/" });

        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<BlobStoreOptions>>().Value;
        options.ServiceUri.ShouldBe(new Uri("https://docprocprod.blob.core.windows.net/"));
        options.EffectiveProvider.ShouldBe(BlobStoreProvider.Azure);
        AzureBlobClients.Resolve(options).ShouldBe(new AzureBlobEndpoint(null, options.ServiceUri));
        provider.GetRequiredService<IBlobStore>().ShouldBeOfType<AzureBlobStore>();
    }

    [Fact]
    public void Explicit_file_system_provider_wins_over_an_injected_connection_string()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Blobs:Provider"] = "FileSystem",
            ["Blobs:RootPath"] = _temp,
            ["ConnectionStrings:blobs"] = "UseDevelopmentStorage=true",
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IBlobStore>().ShouldBeOfType<FileSystemBlobStore>();
    }

    [Fact]
    public void Azure_provider_without_a_connection_or_service_uri_fails_at_start_up()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:Provider"] = "Azure" });

        var error = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain("ConnectionStrings:blobs");
    }

    [Fact]
    public void Sas_only_connection_string_fails_at_start_up_with_a_clear_message()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:blobs"] = "BlobEndpoint=https://docprocdev.blob.core.windows.net/;SharedAccessSignature=sv=2024-11-04&ss=b&srt=sco&sp=rwl&sig=abc",
        });

        var error = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain("SharedAccessSignature");
    }

    [Fact]
    public void Sas_only_connection_string_is_refused_by_resolve()
    {
        var options = new BlobStoreOptions { ConnectionString = "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;SharedAccessSignature=sv=2024-11-04&sig=abc" };

        var error = Should.Throw<InvalidOperationException>(() => AzureBlobClients.Resolve(options));

        error.Message.ShouldContain("SharedAccessSignature");
        error.Message.ShouldContain("account key");
    }

    [Fact]
    public void File_system_provider_without_a_root_path_fails_at_start_up()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:RootPath"] = " " });

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Blobs:RootPath");
    }

    [Fact]
    public void Non_positive_read_url_lifetime_fails_at_start_up()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:RootPath"] = _temp, ["Blobs:MaxReadUrlLifetime"] = "00:00:00" });

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("MaxReadUrlLifetime");
    }

    [Fact]
    public void Read_url_lifetime_above_seven_days_fails_at_start_up()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:RootPath"] = _temp, ["Blobs:MaxReadUrlLifetime"] = "7.00:00:01" });

        var error = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain("MaxReadUrlLifetime");
        error.Message.ShouldContain("7 days");
    }

    [Fact]
    public void Read_url_lifetime_of_exactly_seven_days_is_accepted()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:RootPath"] = _temp, ["Blobs:MaxReadUrlLifetime"] = "7.00:00:00" });

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Registering_twice_adds_the_container_hosted_service_once()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Blobs:RootPath"] = _temp }).Build();
        var services = new ServiceCollection();

        services.AddDocProcBlobs(configuration);
        services.AddDocProcBlobs(configuration);

        services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(BlobContainersHostedService)).ShouldBe(1);
        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().OfType<BlobContainersHostedService>().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("UseDevelopmentStorage=true", false)]
    [InlineData("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=" + DevelopmentStorageKey + ";BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;", false)]
    [InlineData("https://docprocdev.blob.core.windows.net/", true)]
    [InlineData("Endpoint=https://docprocdev.blob.core.windows.net", true)]
    public void Connection_string_shapes_from_the_app_host_are_recognised(string connectionString, bool expectsTokenCredential)
    {
        var endpoint = AzureBlobClients.Resolve(new BlobStoreOptions { Provider = BlobStoreProvider.Azure, ConnectionString = connectionString });

        endpoint.UsesTokenCredential.ShouldBe(expectsTokenCredential);
        if (expectsTokenCredential)
        {
            endpoint.ServiceUri!.Host.ShouldBe("docprocdev.blob.core.windows.net");
        }
        else
        {
            endpoint.ConnectionString.ShouldBe(connectionString);
        }
    }

    [Fact]
    public void Service_uri_alone_means_managed_identity()
    {
        var options = new BlobStoreOptions { Provider = BlobStoreProvider.Azure, ServiceUri = new Uri("https://docprocprod.blob.core.windows.net") };

        AzureBlobClients.Resolve(options).ShouldBe(new AzureBlobEndpoint(null, options.ServiceUri));
        new BlobStoreOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Unrecognised_connection_string_is_an_error()
    {
        Should.Throw<InvalidOperationException>(() =>
            AzureBlobClients.Resolve(new BlobStoreOptions { Provider = BlobStoreProvider.Azure, ConnectionString = "Server=sql;Database=DocProc" }));
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, string? contentRoot = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddDocProcBlobs(configuration, contentRoot is null ? null : new TestHostEnvironment(contentRoot));
        return services.BuildServiceProvider();
    }

    /// <summary>Only the content root matters to <c>AddDocProcBlobs</c>.</summary>
    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DocProc.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
