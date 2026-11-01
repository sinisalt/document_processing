using System.Net;
using System.Text;
using System.Text.Json;

namespace DocProc.Api.Tests;

/// <summary>
/// A request body that cannot be bound is the client's fault and answers 400 on every minimal-API endpoint. In the
/// Development environment the binder throws (<c>RouteHandlerOptions.ThrowOnBadRequest</c>), so without the status
/// mapping in Program.cs the exception handler would answer 500; these tests therefore host the API as Development.
/// </summary>
public sealed class BodyBindingTests
{
    private static readonly Uri TokenUri = new("/dev/token", UriKind.Relative);
    private static readonly Uri KeysUri = new("/api/v1/api-keys", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Empty_body_on_dev_token_is_a_400_problem()
    {
        using var factory = new DevModeFactory { Environment = "Development" };
        using var client = factory.CreateClient();

        using var empty = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(TokenUri, empty, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        problem.GetProperty("status").GetInt32().ShouldBe(400);

        using var malformed = new StringContent("{\"username\":", Encoding.UTF8, "application/json");
        (await client.PostAsync(TokenUri, malformed, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Empty_body_on_api_key_creation_is_a_400_problem()
    {
        using var factory = new DevModeFactory { Environment = "Development" };
        using var admin = await factory.ClientAsAsync("tenant-a-admin", Ct);

        using var empty = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        var response = await admin.PostAsync(KeysUri, empty, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        problem.GetProperty("status").GetInt32().ShouldBe(400);
    }
}
