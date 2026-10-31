using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DocProc.Integration.Tests.Blobs;

/// <summary>
/// The reported case: with a <c>DOCKER_HOST</c> that accepts connections and never answers, <see cref="AzuriteSetupTests"/>
/// built its <c>AzuriteBuilder</c> ungated, the builder waited on Testcontainers' endpoint discovery for Docker.DotNet's
/// default timeout, and the integration run took four and a half minutes instead of seconds. The class runs here in a
/// child test process against such an endpoint, because Testcontainers discovers its endpoint once per process.
/// </summary>
public sealed class AzuriteSetupTimingTests
{
    private const string ChildMarker = "DOCPROC_AZURITE_TIMING_CHILD";

    /// <summary>Generous for a slow CI machine; the unbounded run took 270 s, the bounded one about ten.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task The_azurite_setup_check_finishes_in_seconds_against_a_docker_host_that_never_answers()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(ChildMarker) == "1", "Runs in the parent process only.");
        var ct = TestContext.Current.CancellationToken;

        // Never accepts: connections complete in the backlog and the ping is never answered.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start(backlog: 64);
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var results = Directory.CreateTempSubdirectory("docproc-azurite-timing-").FullName;
        try
        {
            using var child = new Process { StartInfo = ChildStartInfo(port, results) };
            var clock = Stopwatch.StartNew();
            child.Start();
            var output = child.StandardOutput.ReadToEndAsync(ct);
            var errors = child.StandardError.ReadToEndAsync(ct);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(Budget * 2);
                try
                {
                    await child.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    child.Kill(entireProcessTree: true);
                }
            }

            clock.Stop();
            var log = $"{await output}\n{await errors}";
            clock.Elapsed.ShouldBeLessThan(Budget, log);
            log.ShouldContain("total: 1", customMessage: log);
            log.ShouldContain("failed: 0", customMessage: log);
        }
        finally
        {
            silent.Stop();
            Directory.Delete(results, recursive: true);
        }
    }

    /// <summary>This test assembly again, filtered to <see cref="AzuriteSetupTests"/>, with the silent endpoint as its Docker host.</summary>
    private static ProcessStartInfo ChildStartInfo(int port, string resultsDirectory)
    {
        var assembly = typeof(AzuriteSetupTests).Assembly.Location;
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The test host has no process path.");
        var info = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // Started as `dotnet <assembly>.dll` or as the assembly's own apphost.
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(assembly);
        }

        foreach (var argument in new[] { "--filter-class", typeof(AzuriteSetupTests).FullName!, "--results-directory", resultsDirectory, "--no-progress", "--no-ansi" })
        {
            info.ArgumentList.Add(argument);
        }

        // A standalone run: nothing of the parent's `dotnet test` session (its pipe, its options) is handed down.
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        info.Environment["DOCKER_HOST"] = $"tcp://127.0.0.1:{port}";
        info.Environment[ChildMarker] = "1";
        return info;
    }
}
