using System.Reflection;
using DocProc.Infrastructure.Persistence;
using DocProc.ServiceDefaults;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi("v1");
builder.Services.AddProblemDetails();

// Migrate on start-up everywhere except production, where the release pipeline runs the script.
if (builder.Services.TryAddDocProcPersistence(builder.Configuration, migrateByDefault: !builder.Environment.IsProduction()))
{
    builder.Services.AddHealthChecks().AddDbContextCheck<DocProcDbContext>("sql", tags: ["ready"]);
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapDefaultEndpoints();
app.MapOpenApi("/openapi/{documentName}.json");

// Interactive API reference at /scalar (docs/05 "API first": the OpenAPI document is generated from code).
app.MapScalarApiReference(options => options
    .WithTitle("DocProc API")
    .WithTheme(ScalarTheme.Default)
    .WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl));

var v1 = app.MapGroup("/api/v1");
v1.MapGet("/version", () => new
{
    name = "DocProc.Api",
    version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
}).WithName("GetVersion").WithSummary("Build version of the running API.");

app.Run();
