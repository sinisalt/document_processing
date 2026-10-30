using DocProc.Infrastructure.Persistence;
using DocProc.ServiceDefaults;
using DocProc.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// The API owns schema migration; the worker only connects. Same provider switch as the API (docs/09 "Running without
// Docker"), so Persistence:Provider applies to both hosts or to neither.
builder.Services.TryAddDocProcPersistence(builder.Configuration, builder.Environment, migrateByDefault: false);
builder.Services.AddHostedService<PipelineWorker>();

var host = builder.Build();
host.Run();
