using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi("v1");
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi("/openapi/{documentName}.json");
app.MapHealthChecks("/health");

var v1 = app.MapGroup("/api/v1");
v1.MapGet("/version", () => new
{
    name = "DocProc.Api",
    version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
}).WithName("GetVersion");

app.Run();
