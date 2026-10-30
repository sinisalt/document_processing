using DocProc.Infrastructure.Blobs;
using DocProc.Infrastructure.Persistence;
using DocProc.ServiceDefaults;
using DocProc.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// The API owns schema migration; the worker only connects. Same provider switch as the API (docs/09 "Running without
// Docker"), so Persistence:Provider applies to both hosts or to neither.
builder.Services.TryAddDocProcPersistence(builder.Configuration, builder.Environment, migrateByDefault: false);

// Same blob store as the API: Azure/Azurite when the AppHost injects ConnectionStrings:blobs, else ./data/blobs
// under the content root. Both hosts must resolve to the same store or the worker cannot read what the API ingested.
builder.Services.AddDocProcBlobs(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<PipelineWorker>();

var host = builder.Build();
host.Run();
