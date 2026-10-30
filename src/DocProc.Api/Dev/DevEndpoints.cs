using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace DocProc.Api.Dev;

public sealed record DevTokenRequest(string Username);

/// <summary>
/// The development identity endpoints (docs/09 "Running without Docker"). Mapped only while
/// <see cref="Auth.DevAuthOptions.Enabled"/> is true, so a host with real OIDC has no trace of them: not even a 401.
/// </summary>
public static class DevEndpoints
{
    public const string Tag = "Development";

    public static IEndpointRouteBuilder MapDevAuth(this IEndpointRouteBuilder app)
    {
        var dev = app.MapGroup("/dev").WithTags(Tag).AllowAnonymous();

        dev.MapGet("/users", () => TypedResults.Ok(DevUsers.All))
            .WithName("ListDevUsers")
            .WithSummary("The users of the local realm that POST /dev/token can sign a token for.");

        dev.MapPost("/token", Results<Ok<DevTokenResponse>, ValidationProblem, NotFound<ProblemDetails>> (DevTokenRequest request, DevTokenIssuer issuer) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["username"] = ["Username is required."] });
            }

            var user = DevUsers.Find(request.Username);
            if (user is null)
            {
                return TypedResults.NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Unknown development user",
                    Detail = $"No development user '{request.Username}'. Known users: {string.Join(", ", DevUsers.All.Select(u => u.Username))}.",
                });
            }

            return TypedResults.Ok(issuer.Issue(user));
        })
        .WithName("IssueDevToken")
        .WithSummary("Signs a bearer token for a development user; no password, development only.");

        return app;
    }
}
