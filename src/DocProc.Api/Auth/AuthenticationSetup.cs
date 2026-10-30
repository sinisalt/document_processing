using DocProc.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace DocProc.Api.Auth;

public static class AuthenticationSetup
{
    /// <summary>Default scheme: forwards to API-key authentication when a key is presented, otherwise to JWT bearer.</summary>
    public const string DefaultScheme = "DocProc";

    /// <summary>
    /// JWT bearer against the configured authority, API keys for machine clients, role/scope policies from
    /// <see cref="Policies"/>, and a fallback policy that requires an authenticated user on every endpoint not marked
    /// anonymous (secure by default).
    /// </summary>
    public static IServiceCollection AddDocProcAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var oidc = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
        services.AddSingleton(oidc);
        services.AddTenantContext();

        services.AddAuthentication(DefaultScheme)
            .AddPolicyScheme(DefaultScheme, "API key or OIDC bearer", options =>
                options.ForwardDefaultSelector = context =>
                    ApiKeyAuthenticationHandler.HasApiKey(context.Request)
                        ? ApiKeyAuthenticationOptions.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, _ => { })
            .AddJwtBearer(options =>
            {
                if (!string.IsNullOrWhiteSpace(oidc.Authority))
                {
                    options.Authority = oidc.Authority;
                }

                options.Audience = oidc.Audience;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;

                // Keep the provider's claim names (sub, roles, tenant_id) instead of the legacy SOAP-style URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = oidc.Authority,
                    ValidateAudience = true,
                    ValidAudience = oidc.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    RoleClaimType = oidc.RoleClaimType,
                    NameClaimType = oidc.NameClaimType,
                };
            });

        var authorization = services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        foreach (var policy in Policies.All)
        {
            authorization.AddPolicy(policy, p => p.RequireAuthenticatedUser().RequireAssertion(ctx => Policies.Satisfies(ctx.User, policy)));
        }

        services.AddScoped<TenantResolutionMiddleware>();
        return services;
    }
}
