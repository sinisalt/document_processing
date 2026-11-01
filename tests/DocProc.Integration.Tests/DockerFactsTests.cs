using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Configurations;

namespace DocProc.Integration.Tests;

/// <summary>
/// The Docker gate counts a daemon that answers, not a socket path that exists, so the integration tests skip on a
/// machine where Docker Desktop or OrbStack quit and left <c>/var/run/docker.sock</c> behind (docs/09 "Test pyramid").
/// </summary>
public sealed class DockerFactsTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(1);

    private readonly string _temp = Directory.CreateTempSubdirectory("docproc-docker-facts-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void A_socket_symlink_whose_daemon_is_gone_does_not_count_as_docker()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets only.");
        var socket = Path.Combine(_temp, "docker.sock");
        File.CreateSymbolicLink(socket, Path.Combine(_temp, "Docker.app-has-quit", "docker.sock"));
        File.Exists(socket).ShouldBeTrue("the old check accepted exactly this: File.Exists follows no link");

        DockerFacts.Answers(Endpoint($"unix://{socket}"), Timeout).ShouldBeFalse();
    }

    [Fact]
    public void A_socket_file_nobody_listens_on_does_not_count_as_docker()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets only.");
        var path = Path.Combine(_temp, "stale.sock");
        // Bound but never listening: the socket file is there, connecting to it is refused.
        using var bound = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        bound.Bind(new UnixDomainSocketEndPoint(path));
        File.Exists(path).ShouldBeTrue("the old check accepted exactly this");

        DockerFacts.Answers(Endpoint($"unix://{path}"), Timeout).ShouldBeFalse();
    }

    [Fact]
    public void A_docker_host_nobody_listens_on_does_not_count_as_docker()
    {
        var port = UnusedPort();

        DockerFacts.Answers(Endpoint($"tcp://127.0.0.1:{port}"), Timeout).ShouldBeFalse();
    }

    [Fact]
    public async Task A_daemon_that_answers_ping_counts_as_docker()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = AnswerPingAsync(listener, ct);

        var answers = await Task.Run(() => DockerFacts.Answers(Endpoint($"tcp://127.0.0.1:{port}"), Timeout), ct);

        answers.ShouldBeTrue();
        (await server).ShouldContain("/_ping");
    }

    [Fact]
    public void A_docker_host_that_accepts_connections_but_never_answers_is_given_up_on_within_the_timeout()
    {
        using var silent = SilentListener(out var port);
        var clock = Stopwatch.StartNew();

        DockerFacts.Answers(Endpoint($"tcp://127.0.0.1:{port}"), ShortTimeout).ShouldBeFalse();

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Discovery_that_hangs_on_an_endpoint_that_never_answers_counts_as_no_docker_within_the_timeout()
    {
        // What Testcontainers' discovery does for each candidate: build a client and ping it with Docker.DotNet's
        // default timeout (minutes), which a DOCKER_HOST of a stopped VM or of a VPN that is off never answers.
        using var silent = SilentListener(out var port);
        var candidate = Endpoint($"tcp://127.0.0.1:{port}");
        IDockerEndpointAuthenticationConfiguration? Discover()
        {
            using var client = candidate.GetDockerClientBuilder(Guid.Empty).Build();
            client.System.PingAsync().GetAwaiter().GetResult();
            return candidate;
        }

        var clock = Stopwatch.StartNew();

        DockerFacts.Detect(Discover, ShortTimeout).ShouldBeFalse();

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "the gate waits for its own timeout, not for Docker.DotNet's default");
    }

    [Fact]
    public void Discovery_that_finds_no_endpoint_or_throws_counts_as_no_docker()
    {
        DockerFacts.Detect(() => null, Timeout).ShouldBeFalse();
        DockerFacts.Detect(() => throw new InvalidOperationException("a broken ~/.testcontainers.properties"), Timeout).ShouldBeFalse();
    }

    [Fact]
    public async Task Discovery_that_finds_a_daemon_that_answers_counts_as_docker()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = AnswerPingAsync(listener, ct);

        var available = await Task.Run(() => DockerFacts.Detect(() => Endpoint($"tcp://127.0.0.1:{port}"), Timeout), ct);

        available.ShouldBeTrue();
        (await server).ShouldContain("/_ping");
    }

    private static DockerEndpointAuthenticationConfiguration Endpoint(string uri) => new(new Uri(uri));

    /// <summary>A listener that never accepts: the connection completes in the backlog and the request is never answered.</summary>
    private static TcpListener SilentListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 16);
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static int UnusedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>The smallest Docker daemon: one HTTP request, answered <c>200 OK</c> with the body <c>OK</c>.</summary>
    private static async Task<string> AnswerPingAsync(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        var request = new StringBuilder();
        var buffer = new byte[1024];
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 2\r\nApi-Version: 1.44\r\nConnection: close\r\n\r\nOK"u8.ToArray();
        await stream.WriteAsync(response, ct);
        await stream.FlushAsync(ct);
        return request.ToString();
    }
}
