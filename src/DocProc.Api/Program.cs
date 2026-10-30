using System.Reflection;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence;
using DocProc.ServiceDefaults;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer<OpenApiSecurityTransformer>());
builder.Services.AddProblemDetails();
builder.Services.AddDocProcAuthentication(builder.Configuration);

// Migrate on start-up everywhere except production, where the release pipeline runs the script.
if (builder.Services.TryAddDocProcPersistence(builder.Configuration, migrateByDefault: !builder.Environment.IsProduction()))
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

app.Run();

/// <summary>Entry point marker for in-process tests (WebApplicationFactory).</summary>
public partial class Program;
