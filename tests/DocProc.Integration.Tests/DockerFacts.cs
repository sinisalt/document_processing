namespace DocProc.Integration.Tests;

/// <summary>
/// Integration tests need Docker (docs/09 "Test pyramid"). Without it they are skipped, not failed, so the default
/// <c>dotnet test</c> run stays green on a machine without Docker while CI, which has it, exercises them.
/// </summary>
internal static class DockerFacts
{
    public static bool IsAvailable { get; } = Detect();

    public static void SkipUnlessAvailable()
    {
        if (!IsAvailable)
        {
            Assert.Skip("Docker is not available on this machine.");
        }
    }

    private static bool Detect()
    {
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 })
        {
            return true;
        }

        return File.Exists("/var/run/docker.sock")
            || OperatingSystem.IsWindows() && Directory.Exists(@"\\.\pipe\docker_engine");
    }
}
