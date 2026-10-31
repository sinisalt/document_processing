using System.Reflection;
using Testcontainers.Azurite;

namespace DocProc.Integration.Tests.Blobs;

/// <summary>
/// How <see cref="AzureBlobStoreAzuriteTests"/> starts Azurite, checked without Docker: the Docker-gated tests are
/// skipped on a developer machine without Docker, so a start-up that cannot work would otherwise surface only in CI.
/// </summary>
public sealed class AzuriteSetupTests
{
    [Fact]
    public void Azurite_skips_its_api_version_check_and_keeps_the_builder_defaults()
    {
        var command = Configuration(AzureBlobStoreAzuriteTests.NewAzurite()).Command.ToList();

        command.ShouldContain(AzureBlobStoreAzuriteTests.SkipApiVersionCheck);
        // WithCommand appends: the blob host binding the builder sets up must survive, or the mapped port never answers.
        command.ShouldContain("--blobHost");
    }

    private static AzuriteConfiguration Configuration(AzuriteBuilder builder)
    {
        // The builder keeps its configuration in a protected property; the type and its Command are public.
        var property = typeof(AzuriteBuilder).GetProperty("DockerResourceConfiguration", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException("AzuriteBuilder.DockerResourceConfiguration is gone; check the Testcontainers.Azurite upgrade.");
        return (AzuriteConfiguration)property.GetValue(builder)!;
    }
}
