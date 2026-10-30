using System.Security.Claims;
using System.Text.Encodings.Web;
using DocProc.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DocProc.Api.Auth;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string AuthorizationScheme = "ApiKey";
}

/// <summary>
/// Machine-client authentication (docs/05 "Roles"): <c>X-Api-Key: dpk_…</c> or <c>Authorization: ApiKey dpk_…</c>.
/// A valid key becomes a principal with the key's tenant and scopes and no roles, so role policies never match it and
/// scope policies (<see cref="Policies.Ingest"/>, <see cref="Policies.Read"/>) do.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    OidcOptions oidc)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SubjectPrefix = "apikey:";
    public const string ScopeClaimType = "scope";
    public const string AuthMethodClaimType = "auth_method";
    public const string AuthMethodValue = "apikey";

    /// <summary>True when the request carries an API key in either accepted place.</summary>
    public static bool HasApiKey(HttpRequest request) => Extract(request) is not null;

    public static string? Extract(HttpRequest request)
    {
        if (request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var header) && !string.IsNullOrWhiteSpace(header))
        {
            return header.ToString().Trim();
        }

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(ApiKeyAuthenticationOptions.AuthorizationScheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization[(ApiKeyAuthenticationOptions.AuthorizationScheme.Length + 1)..].Trim();
        }

        return null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Extract(Request);
        if (presented is null)
        {
            return AuthenticateResult.NoResult();
        }

        var validator = Context.RequestServices.GetService<IApiKeyValidator>();
        if (validator is null)
        {
            Logger.LogWarning("An API key was presented but API key authentication has no database configured.");
            return AuthenticateResult.Fail("API key authentication is not available.");
        }

        var identity = await validator.ValidateAsync(presented, Context.RequestAborted).ConfigureAwait(false);
        if (identity is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var claims = new List<Claim>
        {
            new("sub", SubjectPrefix + identity.KeyId.ToString("N")),
            new(oidc.NameClaimType, identity.Name),
            new(oidc.TenantClaimType, identity.TenantId.ToString()),
            new(AuthMethodClaimType, AuthMethodValue),
            new("api_key_prefix", identity.Prefix),
        };
        claims.AddRange(identity.Scopes.Select(s => new Claim(ScopeClaimType, s)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, oidc.NameClaimType, oidc.RoleClaimType));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"{ApiKeyAuthenticationOptions.AuthorizationScheme} realm=\"docproc\"";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
