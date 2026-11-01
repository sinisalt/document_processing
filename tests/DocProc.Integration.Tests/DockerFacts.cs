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
/// Testcontainers' discovery pings each candidate with Docker.DotNet's default timeout, which for an endpoint that
/// never answers (a TCP <c>DOCKER_HOST</c> of a stopped VM, a remote host behind a VPN that is off) takes minutes, so
/// discovery and ping are each bounded by <see cref="PingTimeout"/>, and an endpoint not found in time counts as no
/// Docker.
/// </remarks>
internal static class DockerFacts
{
    /// <summary>
    /// What discovery and the ping each get, in seconds. A constant, so a test that bounds its own wait for discovery
    /// by it does not run this type's initializer, and its wait, first.
    /// </summary>
    internal const int TimeoutSeconds = 5;

    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(TimeoutSeconds);

    public static bool IsAvailable { get; } = Detect(() => TestcontainersSettings.OS.DockerEndpointAuthConfig, PingTimeout);

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

    /// <summary>
    /// True when <paramref name="discover"/> finds an endpoint within <paramref name="timeout"/> and a daemon there
    /// answers a ping within <paramref name="timeout"/> too. A discovery still running when the time is up is left to
    /// finish on its own; the gate does not wait for it.
    /// </summary>
    internal static bool Detect(Func<IDockerEndpointAuthenticationConfiguration?> discover, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(discover);
        IDockerEndpointAuthenticationConfiguration? endpoint;
        try
        {
            // Null when none of Testcontainers' candidate endpoints answered its own ping.
            var discovery = Task.Run(discover);
            if (!discovery.Wait(timeout))
            {
                return false;
            }

            endpoint = discovery.Result;
        }
        catch (Exception)
        {
            // A broken Docker configuration is reported as "no Docker", like a missing daemon.
            return false;
        }

        return endpoint is not null && Answers(endpoint, timeout);
    }
}
