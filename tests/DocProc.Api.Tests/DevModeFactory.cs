using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocProc.Api.Dev;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DocProc.Api.Tests;

/// <summary>
/// Hosts the API exactly as `dotnet run` does on a machine without Docker (docs/09 "Running without Docker"):
/// <c>Persistence:Provider=Sqlite</c> against a temporary file, the seed on, and the development token issuer
/// enabled. Nothing is replaced in the container; the tests go through the same configuration a developer uses.
/// </summary>
public sealed class DevModeFactory : WebApplicationFactory<Program>
{
    private static readonly Uri TokenUri = new("/dev/token", UriKind.Relative);

    public DevModeFactory()
    {
        Directory = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(Directory, "docproc.db");
    }

    /// <summary>A factory whose issuer stores its key in the given PEM file, to prove tokens survive a restart.</summary>
    public static DevModeFactory WithSigningKeyFile(string signingKeyPath) => new() { SigningKeyPath = signingKeyPath };

    public string Environment { get; init; } = "Testing";

    public string? SigningKeyPath { get; init; }

    public string Directory { get; }

    public string DatabasePath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("Persistence:Provider", "Sqlite");
        builder.UseSetting("Persistence:SqlitePath", DatabasePath);
        builder.UseSetting("Authentication:Dev:Enabled", "true");
        builder.UseSetting("Authentication:Oidc:Authority", string.Empty);
        // Explicitly unset by default: appsettings.Development.json names a PEM under the content root, and a test must
        // not write a key into the source tree.
        builder.UseSetting("Authentication:Dev:SigningKeyPath", SigningKeyPath ?? string.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }

    public async Task<DevTokenResponse> TokenForAsync(string username, CancellationToken ct)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync(TokenUri, new DevTokenRequest(username), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DevTokenResponse>(ct))!;
    }

    public async Task<HttpClient> ClientAsAsync(string username, CancellationToken ct, string? tenantHeader = null)
    {
        var token = await TokenForAsync(username, ct);
        return ClientWithToken(token.AccessToken, tenantHeader);
    }

    public HttpClient ClientWithToken(string accessToken, string? tenantHeader = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (tenantHeader is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantHeader);
        }

        return client;
    }
}
