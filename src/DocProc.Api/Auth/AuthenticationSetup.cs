using DocProc.Api.Dev;
using DocProc.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace DocProc.Api.Auth;

public static class AuthenticationSetup
{
    /// <summary>Default scheme: forwards to API-key authentication when a key is presented, otherwise to JWT bearer.</summary>
    public const string DefaultScheme = "DocProc";

    /// <summary>
    /// JWT bearer against the configured authority, API keys for machine clients, role/scope policies from
    /// <see cref="Policies"/>, and a fallback policy that requires an authenticated user on every endpoint not marked
    /// anonymous (secure by default). With <see cref="DevAuthOptions.Enabled"/> (never in production) the bearer
    /// handler trusts the in-process <see cref="DevTokenIssuer"/> instead of an OIDC authority (docs/09).
    /// </summary>
    public static IServiceCollection AddDocProcAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var oidc = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
        var dev = configuration.GetSection(DevAuthOptions.SectionName).Get<DevAuthOptions>() ?? new DevAuthOptions();
        if (dev.Enabled && environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{DevAuthOptions.SectionName}:Enabled is true but the environment is Production. The development token issuer signs tokens for anyone who asks and must never run in production; set {DevAuthOptions.SectionName}:Enabled=false and configure {OidcOptions.SectionName}.");
        }

        services.AddSingleton(oidc);
        services.AddSingleton(dev);
        services.AddTenantContext();
        if (dev.Enabled)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<DevTokenIssuer>();
        }

        services.AddAuthentication(DefaultScheme)
            .AddPolicyScheme(DefaultScheme, "API key or OIDC bearer", options =>
                options.ForwardDefaultSelector = context =>
                    ApiKeyAuthenticationHandler.HasApiKey(context.Request)
                        ? ApiKeyAuthenticationOptions.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, _ => { })
            .AddJwtBearer(options =>
            {
                if (!dev.Enabled && !string.IsNullOrWhiteSpace(oidc.Authority))
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
                    ValidIssuer = dev.Enabled ? DevAuthOptions.Issuer : oidc.Authority,
                    ValidateAudience = true,
                    ValidAudience = oidc.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    RoleClaimType = oidc.RoleClaimType,
                    NameClaimType = oidc.NameClaimType,
                };
            });

        if (dev.Enabled)
        {
            // The dev issuer's key replaces metadata discovery; the OIDC authority, if any, is ignored while dev auth is on.
            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<DevTokenIssuer>((options, issuer) => options.TokenValidationParameters.IssuerSigningKey = issuer.SigningKey);
        }

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
