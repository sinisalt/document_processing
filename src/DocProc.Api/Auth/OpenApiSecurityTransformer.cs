using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DocProc.Api.Auth;

/// <summary>Documents the bearer scheme so Scalar offers an "Authorize" box and generated clients know to send a token.</summary>
public sealed class OpenApiSecurityTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "Bearer";
    public const string ApiKeySchemeName = "ApiKey";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "OIDC access token from the configured authority (locally: Keycloak realm 'docproc').",
        };

        document.Components.SecuritySchemes[ApiKeySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthenticationOptions.HeaderName,
            Description = "Tenant API key (dpk_…) for machine clients; scopes ingest and/or read.",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SchemeName, document)] = [] });
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(ApiKeySchemeName, document)] = [] });

        return Task.CompletedTask;
    }
}
