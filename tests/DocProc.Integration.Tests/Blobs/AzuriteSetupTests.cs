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
    public async Task Azurite_skips_its_api_version_check_and_keeps_the_builder_defaults()
    {
        var builder = await BuildWithinDiscoveryTimeoutAsync(AzureBlobStoreAzuriteTests.NewAzurite);
        var command = Configuration(builder).Command.ToList();

        command.ShouldContain(AzureBlobStoreAzuriteTests.SkipApiVersionCheck);
        // WithCommand appends: the blob host binding the builder sets up must survive, or the mapped port never answers.
        command.ShouldContain("--blobHost");
    }

    /// <summary>
    /// A builder's constructor runs Testcontainers' Docker endpoint discovery, which pings every candidate with
    /// Docker.DotNet's default timeout: against an endpoint that accepts but never answers (a TCP <c>DOCKER_HOST</c>
    /// of a stopped VM) that is minutes, and the whole <c>dotnet test</c> run waits for it. The construction gets the
    /// time <see cref="DockerFacts"/> gives discovery, and the test skips when that is not enough. Without any Docker
    /// endpoint, or with a daemon that answers, discovery is quick and the check runs.
    /// </summary>
    internal static async Task<T> BuildWithinDiscoveryTimeoutAsync<T>(Func<T> build)
    {
        var ct = TestContext.Current.CancellationToken;
        var building = Task.Run(build, ct);
        try
        {
            return await building.WaitAsync(TimeSpan.FromSeconds(DockerFacts.TimeoutSeconds), ct);
        }
        catch (TimeoutException)
        {
            // The construction is left to finish on its own, like DockerFacts leaves its discovery.
            Assert.Skip($"Testcontainers' Docker endpoint discovery did not finish within {DockerFacts.TimeoutSeconds} s (an endpoint that never answers); building a container builder waits for it.");
            throw; // not reached: Assert.Skip throws
        }
    }

    private static AzuriteConfiguration Configuration(AzuriteBuilder builder)
    {
        // The builder keeps its configuration in a protected property; the type and its Command are public.
        var property = typeof(AzuriteBuilder).GetProperty("DockerResourceConfiguration", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException("AzuriteBuilder.DockerResourceConfiguration is gone; check the Testcontainers.Azurite upgrade.");
        return (AzuriteConfiguration)property.GetValue(builder)!;
    }
}
