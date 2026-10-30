using System.Reflection;
using DocProc.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi("v1");
builder.Services.AddProblemDetails();
var health = builder.Services.AddHealthChecks();

// Migrate on start-up everywhere except production, where the release pipeline runs the script.
if (builder.Services.TryAddDocProcPersistence(builder.Configuration, migrateByDefault: !builder.Environment.IsProduction()))
{
    health.AddDbContextCheck<DocProcDbContext>("sql", tags: ["ready"]);
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi("/openapi/{documentName}.json");
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

var v1 = app.MapGroup("/api/v1");
v1.MapGet("/version", () => new
{
    name = "DocProc.Api",
    version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
}).WithName("GetVersion");

app.Run();
