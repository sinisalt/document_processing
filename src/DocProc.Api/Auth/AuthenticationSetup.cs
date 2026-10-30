using DocProc.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace DocProc.Api.Auth;

public static class AuthenticationSetup
{
    /// <summary>
    /// JWT bearer against the configured authority, role policies from <see cref="Policies"/>, and a fallback policy that
    /// requires an authenticated user on every endpoint not marked anonymous (secure by default).
    /// </summary>
    public static IServiceCollection AddDocProcAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var oidc = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
        services.AddSingleton(oidc);
        services.AddTenantContext();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddDocProcPolicies();

        services.AddScoped<TenantResolutionMiddleware>();
        return services;
    }

    private static Microsoft.AspNetCore.Authorization.AuthorizationBuilder AddDocProcPolicies(this Microsoft.AspNetCore.Authorization.AuthorizationBuilder builder)
    {
        foreach (var (policy, roles) in Policies.RolesByPolicy)
        {
            builder.AddPolicy(policy, p => p.RequireAuthenticatedUser().RequireRole(roles.ToArray()));
        }

        return builder;
    }
}
