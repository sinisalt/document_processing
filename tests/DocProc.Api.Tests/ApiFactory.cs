using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DocProc.Api.Tests;

/// <summary>
/// Hosts the API in-process with no database and no identity provider: the bearer handler is given a test signing
/// key and issuer instead of discovering them, and tokens are minted locally with the same claims Keycloak emits.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://test-issuer.local/realms/docproc";
    public const string Audience = "docproc-api";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Authentication:Oidc:Authority", string.Empty);
        builder.UseSetting("Authentication:Oidc:Audience", Audience);
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            }));
    }

    public HttpClient ClientFor(TokenSpec? token, string? tenantHeader = null)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Mint(token));
        }

        if (tenantHeader is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantHeader);
        }

        return client;
    }

    public static string Mint(TokenSpec spec)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = spec.Subject,
            ["preferred_username"] = spec.Subject,
            ["email"] = $"{spec.Subject}@example.test",
            ["roles"] = spec.Roles,
        };
        if (spec.TenantId is not null)
        {
            claims["tenant_id"] = spec.TenantId;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = spec.Issuer ?? Issuer,
            Audience = spec.Audience ?? Audience,
            Claims = claims,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = spec.Expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

public sealed record TokenSpec(string Subject, string[] Roles, string? TenantId = null, string? Issuer = null, string? Audience = null, DateTime? Expires = null);
