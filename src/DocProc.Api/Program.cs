using System.Reflection;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Dev;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Blobs;
using DocProc.Infrastructure.Persistence;
using DocProc.Pipeline;
using DocProc.ServiceDefaults;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer<OpenApiSecurityTransformer>());
builder.Services.AddProblemDetails();
builder.Services.AddDocProcAuthentication(builder.Configuration, builder.Environment);

// SQL Server (migrate on start-up everywhere except production, where the release pipeline runs the script) or, for the
// dependency-free development mode, a SQLite file created from the model (docs/09 "Running without Docker").
if (builder.Services.TryAddDocProcPersistence(builder.Configuration, builder.Environment, migrateByDefault: !builder.Environment.IsProduction()))
{
    builder.Services.AddHealthChecks().AddDbContextCheck<DocProcDbContext>("sql", tags: ["ready"]);
}

// Blob storage: Azure/Azurite as soon as the AppHost injects ConnectionStrings:blobs (or Blobs:ServiceUri is set),
// otherwise the content-addressed file store under ./data/blobs in the content root (docs/09 "Running without Docker").
builder.Services.AddDocProcBlobs(builder.Configuration, builder.Environment);
builder.AddDocProcProcessingStack();

var app = builder.Build();

// Every unhandled exception becomes a ProblemDetails. A request that cannot be bound (empty or malformed JSON body:
// BadHttpRequestException from the minimal-API binder when RouteHandlerOptions.ThrowOnBadRequest is on, which is the
// Development default; JsonException from a handler that parses JSON itself) is the client's fault and answers 400,
// not 500. The log stays quiet for those; genuine 5xx are still logged as errors.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception switch
    {
        BadHttpRequestException bad => bad.StatusCode,
        JsonException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    },
    SuppressDiagnosticsCallback = context => context.HttpContext.Response.StatusCode < StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

// Anonymous surface: probes, the OpenAPI document and the Scalar reference. Everything else needs a token (fallback policy).
app.MapDefaultEndpoints();
app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapScalarApiReference(options => options
    .WithTitle("DocProc API")
    .WithTheme(ScalarTheme.Default)
    .WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl))
    .AllowAnonymous();

var v1 = app.MapGroup("/api/v1");
v1.MapGet("/version", () => new
{
    name = "DocProc.Api",
    version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
})
.WithName("GetVersion")
.WithSummary("Build version of the running API.")
.AllowAnonymous();

v1.MapMe();
v1.MapApiKeys();

// The development token issuer exists only while enabled (never in production: AddDocProcAuthentication refuses to start).
if (app.Services.GetRequiredService<DevAuthOptions>().Enabled)
{
    app.Logger.LogWarning("DEVELOPMENT AUTHENTICATION IS ENABLED: POST /dev/token signs a bearer token for any listed user without a password, and the OIDC authority is ignored. Set Authentication:Dev:Enabled=false for anything but a local machine.");

    // Load or create the signing key now, so a corrupt or unreadable PEM fails the start-up instead of the first request.
    _ = app.Services.GetRequiredService<DevTokenIssuer>();
    app.MapDevAuth();
}

app.Run();

/// <summary>Entry point marker for in-process tests (WebApplicationFactory).</summary>
public partial class Program;
