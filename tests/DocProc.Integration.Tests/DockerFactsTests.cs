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

    private static DockerEndpointAuthenticationConfiguration Endpoint(string uri) => new(new Uri(uri));

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
