using DotNet.Testcontainers.Configurations;

namespace DocProc.Integration.Tests;

/// <summary>
/// Integration tests need Docker (docs/09 "Test pyramid"). Without it they are skipped, not failed, so the default
/// <c>dotnet test</c> run stays green on a machine without Docker while CI, which has it, exercises them.
/// </summary>
/// <remarks>
/// "Docker" means a daemon that answers, not a socket path that exists: Docker Desktop and OrbStack leave the
/// <c>/var/run/docker.sock</c> symlink behind when they quit, and a <c>DOCKER_HOST</c> for a stopped colima or podman
/// machine stays set. The endpoint is the one Testcontainers itself discovers (environment, <c>~/.testcontainers.properties</c>,
/// the Docker context, the default sockets), so the tests skip exactly when the containers could not start.
/// </remarks>
internal static class DockerFacts
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    public static bool IsAvailable { get; } = Detect();

    public static void SkipUnlessAvailable()
    {
        if (!IsAvailable)
        {
            Assert.Skip("Docker is not available on this machine (no Docker daemon answered).");
        }
    }

    /// <summary>True when a Docker daemon answers <c>/_ping</c> at <paramref name="endpoint"/> within <paramref name="timeout"/>.</summary>
    internal static bool Answers(IDockerEndpointAuthenticationConfiguration endpoint, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        try
        {
            using var client = endpoint.GetDockerClientBuilder(Guid.Empty).Build();
            using var cts = new CancellationTokenSource(timeout);
            client.System.PingAsync(cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception)
        {
            // Any failure to reach the daemon means "no Docker": the tests skip instead of failing.
            return false;
        }
    }

    private static bool Detect()
    {
        IDockerEndpointAuthenticationConfiguration? endpoint;
        try
        {
            // Null when none of Testcontainers' candidate endpoints answered its own ping.
            endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig;
        }
        catch (Exception)
        {
            // A broken Docker configuration is reported as "no Docker", like a missing daemon.
            return false;
        }

        return endpoint is not null && Answers(endpoint, PingTimeout);
    }
}
