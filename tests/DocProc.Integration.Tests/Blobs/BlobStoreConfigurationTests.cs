using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocProc.Integration.Tests.Blobs;

/// <summary><c>AddDocProcBlobs</c>: provider selection from configuration and fail-fast validation at start-up.</summary>
public sealed class BlobStoreConfigurationTests
{
    [Fact]
    public void Defaults_to_the_file_system_store_under_data_blobs()
    {
        using var provider = Build([]);

        var store = provider.GetRequiredService<IBlobStore>();

        var fileSystem = store.ShouldBeOfType<FileSystemBlobStore>();
        fileSystem.RootPath.ShouldBe(Path.GetFullPath("./data/blobs"));
        provider.GetRequiredService<IContainerBlobStore>().ShouldBeSameAs(store);
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void File_system_root_path_comes_from_configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"));
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:Provider"] = "FileSystem", ["Blobs:RootPath"] = root });

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IBlobStore>().ShouldBeOfType<FileSystemBlobStore>().RootPath.ShouldBe(Path.GetFullPath(root));
        Directory.Exists(root).ShouldBeTrue();
        Directory.Delete(root, recursive: true);
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
    public void Azure_provider_without_a_connection_or_service_uri_fails_at_start_up()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:Provider"] = "Azure" });

        var error = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain("ConnectionStrings:blobs");
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
        using var provider = Build(new Dictionary<string, string?> { ["Blobs:MaxReadUrlLifetime"] = "00:00:00" });

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("MaxReadUrlLifetime");
    }

    [Theory]
    [InlineData("UseDevelopmentStorage=true", false)]
    [InlineData("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;", false)]
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

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddDocProcBlobs(configuration);
        return services.BuildServiceProvider();
    }
}
