using System.Reflection;
using DocProc.Api.Auth;
using DocProc.Api.Dev;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence;
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

var app = builder.Build();

app.UseExceptionHandler();
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
    app.MapDevAuth();
}

app.Run();

/// <summary>Entry point marker for in-process tests (WebApplicationFactory).</summary>
public partial class Program;
